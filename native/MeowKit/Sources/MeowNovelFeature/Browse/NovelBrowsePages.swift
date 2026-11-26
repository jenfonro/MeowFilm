#if os(iOS)
import UIKit
import Combine
import MeowDebug
import MeowUI

@MainActor
public final class NovelHomeViewController: PosterHomeViewController {
    public var onSelect: ((NovelBook) -> Void)?
    public var onSelectCategory: ((NovelCategoryModel) -> Void)?
    public let discovery: NovelDiscoverySession

    public init(feature: NovelFeature, themeProvider: any PageThemeProviding) {
        discovery = NovelDiscoverySession(feature: feature)
        super.init(
            configuration: PosterHomeConfiguration(
                pageAccessibilityID: "novel.home",
                collectionAccessibilityID: "novel.home.sections",
                moreIdentifierPrefix: "novel.home.more"
            ),
            themeProvider: themeProvider
        )
        pageTitle = "小说"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        let discoveryChanges = Publishers.MergeMany([
            discovery.$selectedSource.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            discovery.$sections.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            discovery.$loading.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            discovery.$error.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ]).eraseToAnyPublisher()
        observe(discoveryChanges, whenVisible: true) { [weak self] in self?.render() }
        observe(discovery.feature.$error.dropFirst().map { _ in }.eraseToAnyPublisher(), whenVisible: true) { [weak self] in
            self?.presentErrors()
        }
        render()
    }

    public override func pageDidBecomeVisible() {
        discovery.activate()
        render()
        super.pageDidBecomeVisible()
    }

    public override func pageDidBecomeHidden() {
        discovery.deactivate()
        super.pageDidBecomeHidden()
    }

    public override func pageDidBecomeInactive() {
        discovery.deactivate()
        super.pageDidBecomeInactive()
        Task { [discovery] in await discovery.close() }
    }

    public override func didRequestRefresh() {
        clearErrorSource("discovery")
        discovery.refresh()
    }

    public override func didDisplaySection(sectionID: String) {
        discovery.admitSection(id: sectionID)
    }

    public override func didTapSectionMore(sectionID: String) {
        guard let category = discovery.categoryModel(sectionID: sectionID) else { return }
        onSelectCategory?(category)
    }

    public override func didSelectCard(inSection sectionID: String, cardID: String, index: Int) {
        guard let section = discovery.sections.first(where: { $0.id == sectionID }) else { return }
        let book: NovelBook?
        if section.books.indices.contains(index),
           Self.cardID(for: section.books[index]) == cardID {
            book = section.books[index]
        } else {
            book = section.books.first { Self.cardID(for: $0) == cardID }
        }
        guard let book else { return }
        onSelect?(book)
    }

    static func cardID(for book: NovelBook) -> String {
        "\(book.installationId)\u{1F}\(book.bookUrl)"
    }

    static func makePosterCard(
        _ book: NovelBook,
        feature: NovelFeature,
        textBadge: String = "",
        label: String = ""
    ) -> PosterCatalogCard {
        let source = try? feature.source(for: book)
        let coverKey: NovelImageStore.Key? = if let source, !book.coverUrl.isEmpty {
            NovelImageStore.Key(source: source, bookUrl: book.bookUrl, url: book.coverUrl, kind: "cover")
        } else {
            nil
        }
        let artwork = PosterCatalogArtwork(
            id: coverKey.map { AnyHashable($0) },
            placeholderSymbol: "book.closed",
            cached: { [weak feature] in
                guard let feature, let source, !book.coverUrl.isEmpty else { return nil }
                return feature.images.cached(source: source, bookUrl: book.bookUrl, url: book.coverUrl, kind: "cover")
            },
            load: { [weak feature] in
                guard let feature, let source, !book.coverUrl.isEmpty, feature.accepts(source) else { return nil }
                do {
                    let image = try await feature.images.read(source: source, book: book, url: book.coverUrl, kind: "cover")
                    guard !Task.isCancelled, feature.accepts(source) else { return nil }
                    return image
                } catch {
                    if (error as? NovelError) == .imageReleaseFailed {
                        feature.error = error.localizedDescription
                    }
                    if !(error is CancellationError) && !Task.isCancelled {
                        DebugRuntime.logError(.novel, sub: "cover", event: "load.failed", error: error, data: .object([
                            "sourceId": .string(source.id),
                            "coverUrl": .string(book.coverUrl)
                        ]), level: .warn)
                    }
                    return nil
                }
            }
        )
        return PosterCatalogCard(
            id: cardID(for: book),
            title: book.name,
            textBadge: textBadge,
            label: label,
            accessibilityIdentifier: "novel.poster.\(book.id)",
            artwork: artwork
        )
    }

    private func render() {
        guard isViewLoaded else { return }
        let feature = discovery.feature
        let projected = discovery.sections.map { section in
            PosterCatalogSection(
                id: section.id,
                title: section.title,
                hasMore: !section.options.isEmpty,
                isLoading: section.state == .pending || section.state == .loading,
                statusText: section.statusText,
                items: section.books.map { Self.makePosterCard($0, feature: feature) }
            )
        }
        let emptyText = discovery.selectedSource == nil ? "没有可发现书源" : "暂无可用分类"
        applyHomeState(
            sourceID: discovery.selectedSource.map { "\($0.id)\u{1F}\($0.revision)" },
            sections: projected,
            isLoading: discovery.loading,
            hasError: discovery.error != nil,
            emptyText: emptyText
        )
        presentErrors()
    }

    private func presentErrors() {
        guard isPageVisible else { return }
        syncErrorSource("discovery", message: discovery.error, retry: { [weak self] in
            self?.discovery.retry()
        })
        if let error = discovery.feature.error {
            discovery.feature.error = nil
            enqueueError(error)
        }
    }

    deinit {
        let discovery = discovery
        Task { await discovery.close() }
    }
}

@MainActor
public final class NovelCategoryViewController: PosterCategoryViewController {
    public var onSelectItem: ((NovelBook) -> Void)?
    public let model: NovelCategoryModel
    private let feature: NovelFeature
    var renderedBooks: [NovelBook] = []

    public init(model: NovelCategoryModel, feature: NovelFeature, themeProvider: any PageThemeProviding) {
        self.model = model
        self.feature = feature
        super.init(
            configuration: PosterCategoryConfiguration(
                pageAccessibilityID: "novel.category.\(model.sectionID)",
                collectionAccessibilityID: "novel.category.grid",
                filterIdentifierPrefix: "novel.category",
                moreButtonAccessibilityID: "novel.category.more",
                loadingAccessibilityID: "novel.category.loading",
                emptyText: "暂无书籍"
            ),
            themeProvider: themeProvider
        )
        pageTitle = model.title
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        observe(model, whenVisible: true) { [weak self] in self?.render() }
        observe(feature.$error.dropFirst().map { _ in }.eraseToAnyPublisher(), whenVisible: true) { [weak self] in
            self?.presentErrors()
        }
        render()
    }

    public override func pageDidBecomeVisible() {
        model.loadIfNeeded()
        render()
        super.pageDidBecomeVisible()
    }

    public override func pageDidBecomeHidden() {
        model.cancel()
        super.pageDidBecomeHidden()
    }

    public override func pageDidBecomeInactive() {
        model.cancel()
        super.pageDidBecomeInactive()
        let model = model
        Task { await model.close() }
    }

    public override func didSelectCard(cardID: String, index: Int) {
        let book: NovelBook?
        if renderedBooks.indices.contains(index),
           NovelHomeViewController.cardID(for: renderedBooks[index]) == cardID {
            book = renderedBooks[index]
        } else {
            book = renderedBooks.first { NovelHomeViewController.cardID(for: $0) == cardID }
        }
        guard let book else { return }
        onSelectItem?(book)
    }

    public override func didSelectFilter(rowID: String, value: String) {
        clearErrorSource("novel.category")
        model.selectOption(value)
    }

    public override func didRequestRefresh() {
        clearErrorSource("novel.category")
        model.refresh()
    }

    public override func didRequestLoadMore() {
        clearErrorSource("novel.category")
        model.loadMore()
    }

    private func render() {
        guard isViewLoaded else { return }
        renderedBooks = model.books
        let filterRows: [PosterCategoryFilterRowModel] = if model.options.count > 1 {
            [
                PosterCategoryFilterRowModel(
                    id: "category",
                    title: "分类",
                    selectedID: model.selectedOptionID,
                    options: model.options.map { FilterTag(id: $0.id, title: $0.title) }
                )
            ]
        } else {
            []
        }
        let cards = renderedBooks.map { NovelHomeViewController.makePosterCard($0, feature: feature) }
        applyCategoryState(
            filterRows: filterRows,
            cards: cards,
            isLoading: model.isLoading,
            hasMore: model.hasMore,
            hasError: model.error != nil
        )
        presentErrors()
    }

    private func presentErrors() {
        guard isPageVisible else { return }
        let retry: (() -> Void)? = model.hasMore ? { [weak self] in self?.model.loadMore() } : nil
        syncErrorSource(
            "novel.category",
            message: model.error,
            token: model.error.map { AnyHashable($0) },
            retry: retry
        )
        if let error = feature.error {
            feature.error = nil
            enqueueError(error)
        }
    }

    deinit {
        let model = model
        Task { await model.close() }
    }
}

@MainActor
public final class NovelSearchResultsContent: UIViewController, SearchResultContent, UITableViewDelegate {
    public var controller: UIViewController { self }
    public var onSelect: ((NovelBook) -> Void)?
    public let session: NovelSearchSession
    private let table = UITableView(frame: .zero, style: .plain)
    private var dataSource: UITableViewDiffableDataSource<Int, String>!
    private let more = UIButton(type: .system), cancelButton = UIButton(type: .system), sources = UIButton(type: .system)
    private let emptyLabel = UILabel()
    private let loadingIndicator = UIActivityIndicatorView(style: .medium)
    private var theme: PageTheme
    private var subscription: AnyCancellable?
    private var active = false
    private var renderedStates: [NovelSearchSourceState]?
    private var rowProjection = IndexedDiffableProjection<Int, String, NovelSearchBook>()
    var rows: [NovelSearchBook] { session.books }
    public init(feature: NovelFeature, theme: PageTheme, matchingBook: NovelBook? = nil) {
        session = NovelSearchSession(feature: feature, matchingBook: matchingBook); self.theme = theme
        super.init(nibName: nil, bundle: nil)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    public override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.search.results"; view.backgroundColor = .clear
        table.backgroundColor = .clear; table.delegate = self; table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 130; table.separatorStyle = .none
        table.accessibilityIdentifier = "novel.search.books"
        table.register(NovelBookCell.self, forCellReuseIdentifier: NovelBookCell.reuseIdentifier)
        dataSource = UITableViewDiffableDataSource<Int, String>(tableView: table) { [weak self] tableView, indexPath, rowID in
            let cell = tableView.dequeueReusableCell(withIdentifier: NovelBookCell.reuseIdentifier, for: indexPath) as! NovelBookCell
            if let self, let row = self.rowProjection[rowID] {
                let isCurrent = self.session.matchingBook?.installationId == row.book.installationId
                cell.configure(row.book, sourceCount: row.candidates.count, isCurrentSource: isCurrent, feature: self.session.feature,
                               theme: self.theme, loadCover: self.active && self.view.window != nil)
            }
            return cell
        }
        dataSource.defaultRowAnimation = .none
        more.setTitle("加载更多", for: .normal); more.accessibilityIdentifier = "novel.search.more"
        cancelButton.setTitle("取消", for: .normal); sources.setTitle("书源", for: .normal); sources.showsMenuAsPrimaryAction = true
        more.addAction(UIAction { [weak self] _ in self?.active = true; self?.session.loadMore() }, for: .touchUpInside)
        cancelButton.addAction(UIAction { [weak self] _ in self?.cancel() }, for: .touchUpInside)
        emptyLabel.textAlignment = .center
        loadingIndicator.hidesWhenStopped = true
        let footer = UIStackView(arrangedSubviews: [sources, cancelButton, more]); footer.distribution = .fillEqually
        let stack = UIStackView(arrangedSubviews: [table, footer]); stack.axis = .vertical
        view.addSubview(stack); stack.pinEdges(to: view)
        footer.heightAnchor.constraint(equalToConstant: 48).isActive = true
        let sessionChanges = Publishers.MergeMany([
            session.$books.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            session.$states.dropFirst().removeDuplicates().map { _ -> Void in }.eraseToAnyPublisher(),
            session.$loading.dropFirst().removeDuplicates().map { _ -> Void in }.eraseToAnyPublisher(),
            session.$error.dropFirst().removeDuplicates().map { _ -> Void in }.eraseToAnyPublisher(),
        ])
        subscription = sessionChanges.receive(on: RunLoop.main).sink { [weak self] in
            guard let self, self.isViewLoaded, self.view.window != nil else { return }
            self.render()
        }
        applyTheme(theme)
        render()
    }
    public func submit(_ query: String) {
        DebugRuntime.log(.search, sub: "novel", event: "submit", level: .debug, data: .object([
            "query": .string(query)
        ]))
        active = true; session.submit(query); if isViewLoaded { render() }
    }
    public override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        if !session.query.isEmpty { submit(session.query) }
        else { render() }
    }
    public override func viewDidDisappear(_ animated: Bool) { super.viewDidDisappear(animated); cancel() }
    public func cancel() { active = false; session.cancel(); if isViewLoaded { for cell in table.visibleCells { (cell as? NovelBookCell)?.cancelPendingLoad() } } }
    public func close() {
        cancel()
        Task { [session] in await session.close() }
    }
    public func applyTheme(_ theme: PageTheme) {
        self.theme = theme
        guard isViewLoaded else { return }
        emptyLabel.textColor = UIColor(theme.muted)
        loadingIndicator.color = UIColor(theme.muted)
        for button in [sources, cancelButton, more] { button.tintColor = UIColor(theme.ink) }
        for cell in table.visibleCells { (cell as? NovelBookCell)?.applyTheme(theme) }
    }
    private func render() {
        applyRowsSnapshot(rows)
        more.isEnabled = !session.loading && session.canLoadMore; more.isHidden = !session.canLoadMore
        cancelButton.isHidden = !session.loading
        sources.isEnabled = !session.states.isEmpty
        if renderedStates != session.states {
            renderedStates = session.states
            sources.menu = session.states.isEmpty ? nil : UIMenu(children: session.states.map { item in
                let status: String
                switch item.status { case .idle: status = "未开始"; case .loading: status = "加载中"; case .loaded: status = "第\(item.page)页";
                case .exhausted: status = "已加载全部"; case .cancelled: status = "已取消"; case .failed(let error): status = error }
                return UIAction(title: item.source.name, subtitle: status) { [weak self] _ in self?.active = true; self?.session.loadMore() }
            })
        }
        if rows.isEmpty, session.loading {
            loadingIndicator.color = UIColor(theme.muted)
            loadingIndicator.startAnimating()
            table.backgroundView = loadingIndicator
        } else if rows.isEmpty {
            loadingIndicator.stopAnimating()
            emptyLabel.text = session.states.isEmpty ? "没有可搜索书源" :
                session.allSourcesFailed ? "搜索失败" :
                session.allSourcesCancelled ? "搜索已取消" : "未找到书籍"
            table.backgroundView = emptyLabel
        } else {
            loadingIndicator.stopAnimating()
            table.backgroundView = nil
        }
        if let error = session.error, presentError(error) { session.error = nil }
    }
    private func presentError(_ error: String) -> Bool {
        var ancestor = parent
        while let page = ancestor {
            if let page = page as? PageViewController {
                page.enqueueError(error)
                return true
            }
            ancestor = page.parent
        }
        return false
    }
    private func searchRowID(_ row: NovelSearchBook) -> String {
        "\(row.book.installationId)\u{1F}\(row.book.bookUrl)"
    }

    private func applyRowsSnapshot(_ items: [NovelSearchBook]) {
        let update = rowProjection.update(
            section: 0,
            items: items,
            id: searchRowID,
            isChanged: { prev, next in
                prev.book != next.book || prev.candidates.count != next.candidates.count
            }
        )
        dataSource.applyIfNeeded(update, animatingDifferences: false)
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard let rowID = dataSource.itemIdentifier(for: indexPath), let row = rowProjection[rowID] else { return }
        onSelect?(row.book)
    }
    public func tableView(_ tableView: UITableView, contextMenuConfigurationForRowAt indexPath: IndexPath, point: CGPoint) -> UIContextMenuConfiguration? {
        guard let rowID = dataSource.itemIdentifier(for: indexPath), let row = rowProjection[rowID], row.candidates.count > 1 else { return nil }
        return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
            UIMenu(children: row.candidates.map { candidate in
                UIAction(title: self?.session.feature.sources.first { $0.id == candidate.installationId }?.name ?? candidate.installationId) { [weak self] _ in self?.onSelect?(candidate) }
            })
        }
    }
    public func tableView(_ tableView: UITableView, didEndDisplaying cell: UITableViewCell, forRowAt indexPath: IndexPath) {
        (cell as? NovelBookCell)?.cancelPendingLoad()
    }
    deinit { let session = session; Task { await session.close() } }
}
#endif
