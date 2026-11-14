#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class NovelHomeViewController: PageViewController, UITableViewDataSource, UITableViewDelegate, UISearchBarDelegate {
    public var onSelect: ((NovelBook) -> Void)?
    private let feature: NovelFeature
    private let sections = UISegmentedControl(items: ["首页", "书架"])
    private let membership = UISegmentedControl(items: ["书架", "历史"])
    private let filter = UISearchBar()
    private let sourceButton = UIButton(type: .system), kindButton = UIButton(type: .system)
    private let more = UIButton(type: .system)
    private let table = UITableView(frame: .zero, style: .plain)
    private var discovered: [NovelBook] = [], entries: [NovelLibraryEntry] = []
    private var selectedSource: NovelSource?, kinds: [NovelExploreKind] = [], kind: NovelExploreKind?
    private var page = 0, exhausted = false, loading = false
    private var pending: Task<Void, Never>?
    private var refreshing: Task<Void, Never>?
    private var renderedSources: [NovelSource] = []
    private var generation = UUID()
    public init(feature: NovelFeature, themeProvider: any PageThemeProviding) {
        self.feature = feature; super.init(themeProvider: themeProvider)
    }
    public override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.home"
        sections.selectedSegmentIndex = 0; sections.accessibilityIdentifier = "novel.sections"
        membership.selectedSegmentIndex = 0; membership.accessibilityIdentifier = "novel.membership"
        sections.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            switchSection(sections.selectedSegmentIndex)
        }, for: .valueChanged)
        membership.addAction(UIAction { [weak self] _ in self?.render() }, for: .valueChanged)
        filter.delegate = self; filter.placeholder = "搜索书架和历史"; filter.backgroundImage = UIImage()
        sourceButton.showsMenuAsPrimaryAction = true; kindButton.showsMenuAsPrimaryAction = true
        more.setTitle("加载更多", for: .normal); more.accessibilityIdentifier = "novel.explore.more"
        more.addAction(UIAction { [weak self] _ in self?.loadExplore(reset: false) }, for: .touchUpInside)
        table.dataSource = self; table.delegate = self; table.backgroundColor = .clear
        table.rowHeight = 96; table.separatorStyle = .none; table.keyboardDismissMode = .onDrag
        table.accessibilityIdentifier = "novel.books"
        table.register(NovelBookCell.self, forCellReuseIdentifier: "book")
        table.refreshControl = UIRefreshControl()
        table.refreshControl?.addAction(UIAction { [weak self] _ in self?.refresh() }, for: .valueChanged)
        let selectors = UIStackView(arrangedSubviews: [sourceButton, kindButton])
        selectors.distribution = .fillEqually
        let stack = UIStackView(arrangedSubviews: [sections, selectors, membership, filter, table, more])
        stack.axis = .vertical; stack.spacing = 8
        view.addSubview(stack); stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: contentTopAnchor, constant: 8), stack.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
            stack.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 16),
            stack.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -16),
            sections.heightAnchor.constraint(equalToConstant: 36), selectors.heightAnchor.constraint(equalToConstant: 36),
            membership.heightAnchor.constraint(equalToConstant: 36), more.heightAnchor.constraint(equalToConstant: 44)])
        observe(feature) { [weak self] in
            Task { @MainActor [weak self] in
                guard let self else { return }
                if renderedSources != feature.sources {
                    renderedSources = feature.sources
                    if selectedSource == nil || selectedSource.map({ !feature.accepts($0) || !$0.enabledExplore }) == true {
                        cancelExplore(); discovered = []; kinds = []; kind = nil; selectedSource = nil
                        if isPageVisible, sections.selectedSegmentIndex == 0 { refresh() }
                    }
                }
                render()
            }
        }
        render()
    }
    public override func pageDidBecomeVisible() {
        feature.start(); refresh()
    }
    public override func pageDidFinishAppearing() { render() }
    public override func pageDidBecomeHidden() { refreshing?.cancel(); refreshing = nil; cancelExplore(); cancelCovers() }
    public override func pageDidBecomeInactive() { refreshing?.cancel(); refreshing = nil; cancelExplore(); cancelCovers() }
    public override func applyTheme() { super.applyTheme(); if isViewLoaded { table.reloadData() } }
    public func showShelf() { loadViewIfNeeded(); switchSection(1) }
    public func showHome() { loadViewIfNeeded(); switchSection(0) }
    private func switchSection(_ index: Int) {
        sections.selectedSegmentIndex = index; refreshing?.cancel(); refreshing = nil; cancelExplore()
        if isPageVisible { refresh() }; render()
    }
    public func searchBar(_ searchBar: UISearchBar, textDidChange searchText: String) { render() }
    private func refresh() {
        refreshing?.cancel()
        refreshing = Task { [weak self] in
            guard let self else { return }
            defer { table.refreshControl?.endRefreshing() }
            do {
                try await feature.reloadLocal()
                guard sections.selectedSegmentIndex == 0, isPageVisible, !Task.isCancelled else { return }
                try await feature.refreshSources()
                guard !Task.isCancelled else { return }
                let available = feature.sources.filter { $0.enabled && $0.enabledExplore && $0.actions.contains("explore") }
                selectedSource = available.first(where: { $0.id == selectedSource?.id }) ?? available.first
                if let selectedSource { try await loadKinds(selectedSource) }
                else { cancelExplore(); discovered = []; kinds = []; kind = nil }
                render()
            } catch { if !Task.isCancelled { feature.error = error.localizedDescription; render() } }
        }
    }
    private func loadKinds(_ source: NovelSource) async throws {
        guard sections.selectedSegmentIndex == 0, isPageVisible else { return }
        cancelExplore(); let token = generation
        let values = try await feature.sourcesClient.kinds(source)
        guard !Task.isCancelled, generation == token, feature.accepts(source) else { return }
        kinds = values; kind = kinds.first { $0.url == kind?.url && !$0.url.isEmpty } ?? kinds.first { !$0.url.isEmpty }
        loadExplore(reset: true)
    }
    private func loadExplore(reset: Bool) {
        guard sections.selectedSegmentIndex == 0, isPageVisible, let source = selectedSource, let kind, !loading || reset else { return }
        if reset { cancelExplore(); discovered = []; page = 0; exhausted = false }
        guard !exhausted else { return }
        let token = generation, nextPage = page + 1
        loading = true; render()
        pending = Task { [weak self] in
            guard let self else { return }
            defer { if token == generation { loading = false; render() } }
            do {
                let books = try await feature.sourcesClient.explore(source, url: kind.url, page: nextPage)
                guard !Task.isCancelled, token == generation, feature.accepts(source) else { return }
                let existing = Set(discovered.map(\.bookUrl)), added = books.filter { !existing.contains($0.bookUrl) }
                discovered += added; page = nextPage; exhausted = added.isEmpty
            } catch { if token == generation, !Task.isCancelled { feature.error = error.localizedDescription; render() } }
        }
    }
    private func cancelExplore() { generation = UUID(); pending?.cancel(); pending = nil; loading = false }
    private func cancelCovers() { for cell in table.visibleCells { (cell as? NovelBookCell)?.cancelImage() } }
    private func render() {
        guard isViewLoaded else { return }
        let shelf = sections.selectedSegmentIndex == 1
        membership.isHidden = !shelf; filter.isHidden = !shelf
        sourceButton.superview?.isHidden = shelf; more.isHidden = shelf || exhausted || selectedSource == nil
        more.isEnabled = !loading
        entries = feature.local.books(history: membership.selectedSegmentIndex == 1, query: filter.text ?? "")
        sourceButton.setTitle(selectedSource?.name ?? "书源", for: .normal)
        sourceButton.menu = UIMenu(children: feature.sources.filter { $0.enabled && $0.enabledExplore && $0.actions.contains("explore") }.map { source in
            UIAction(title: source.name, state: source.id == selectedSource?.id ? .on : .off) { [weak self] _ in
                guard let self else { return }; selectedSource = source; kind = nil
                refreshing?.cancel()
                refreshing = Task { [weak self] in
                    do { try await self?.loadKinds(source) }
                    catch { if !Task.isCancelled { self?.showError(error.localizedDescription) } }
                }
            }
        })
        kindButton.setTitle(kind?.title ?? "分类", for: .normal)
        kindButton.menu = UIMenu(children: kinds.map { item in
            let action = UIAction(title: item.title, attributes: item.url.isEmpty ? .disabled : [],
                state: item.url == kind?.url ? .on : .off) { [weak self] _ in self?.kind = item; self?.loadExplore(reset: true) }
            return action
        })
        table.reloadData()
        let empty = UILabel(); empty.textAlignment = .center; empty.textColor = UIColor(theme.muted)
        empty.accessibilityIdentifier = "novel.books.empty"
        empty.text = shelf ? (membership.selectedSegmentIndex == 1 ? "暂无阅读历史" : "书架为空") :
            (loading ? "加载中" : selectedSource == nil ? "没有可发现书源" : "暂无书籍")
        table.backgroundView = (shelf ? entries.isEmpty : discovered.isEmpty) ? empty : nil
        if let error = feature.error { if showError(error) { feature.error = nil } }
    }
    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        sections.selectedSegmentIndex == 1 ? entries.count : discovered.count
    }
    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let cell = tableView.dequeueReusableCell(withIdentifier: "book", for: indexPath) as! NovelBookCell
        let shelf = sections.selectedSegmentIndex == 1
        let book = shelf ? entries[indexPath.row].book : discovered[indexPath.row]
        cell.configure(book, subtitle: shelf ? entries[indexPath.row].anchor?.chapterTitle : nil, feature: feature, theme: theme, loadCover: isPageVisible)
        return cell
    }
    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        onSelect?(sections.selectedSegmentIndex == 1 ? entries[indexPath.row].book : discovered[indexPath.row])
    }
    public func tableView(_ tableView: UITableView, didEndDisplaying cell: UITableViewCell, forRowAt indexPath: IndexPath) { (cell as? NovelBookCell)?.cancelImage() }
    public func tableView(_ tableView: UITableView, contextMenuConfigurationForRowAt indexPath: IndexPath, point: CGPoint) -> UIContextMenuConfiguration? {
        guard sections.selectedSegmentIndex == 1 else { return nil }
        let entry = entries[indexPath.row], history = membership.selectedSegmentIndex == 1
        return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
            guard let self else { return nil }
            let actions: [UIAction]
            if history {
                actions = [UIAction(title: entry.onShelf ? "移出书架" : "加入书架") { [weak self] _ in self?.mutate { try await self?.feature.setShelf(entry.book, present: !entry.onShelf) } },
                    UIAction(title: "删除历史", attributes: .destructive) { [weak self] _ in self?.mutate { try await self?.feature.deleteHistory(entry.id) } }]
            } else {
                actions = [UIAction(title: entry.pinnedAt == nil ? "置顶" : "取消置顶") { [weak self] _ in self?.mutate { try await self?.feature.pin(entry.id) } },
                    UIAction(title: "移出书架", attributes: .destructive) { [weak self] _ in self?.mutate { try await self?.feature.setShelf(entry.book, present: false) } }]
            }
            return UIMenu(children: actions)
        }
    }
    private func mutate(_ operation: @escaping @MainActor () async throws -> Void) {
        runPageTask { [weak self] in do { try await operation() } catch { self?.showError(error.localizedDescription) } }
    }
}

@MainActor
final class NovelBookCell: UITableViewCell {
    private var pending: Task<Void, Never>?
    private var key = UUID()
    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) { super.init(style: .subtitle, reuseIdentifier: reuseIdentifier) }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func prepareForReuse() { super.prepareForReuse(); cancelImage() }
    func cancelImage() { key = UUID(); pending?.cancel(); pending = nil; imageView?.image = nil }
    func configure(_ book: NovelBook, subtitle: String? = nil, feature: NovelFeature, theme: PageTheme, loadCover: Bool) {
        cancelImage(); let token = key
        backgroundColor = .clear; textLabel?.text = book.name; textLabel?.textColor = UIColor(theme.ink)
        textLabel?.font = UIFontMetrics(forTextStyle: .headline).scaledFont(for: .systemFont(ofSize: 17), compatibleWith: traitCollection)
        detailTextLabel?.text = [book.author, subtitle ?? book.latestChapterTitle].filter { !$0.isEmpty }.joined(separator: " · ")
        detailTextLabel?.textColor = UIColor(theme.muted); detailTextLabel?.numberOfLines = 2
        imageView?.contentMode = .scaleAspectFit; imageView?.image = UIImage(systemName: "book.closed")
        accessoryType = .disclosureIndicator
        guard loadCover, !book.coverUrl.isEmpty, let source = try? feature.source(for: book) else { return }
        pending = Task { [weak self] in
            do {
                let bytes = try await feature.sourcesClient.readImage(source: source, book: book, url: book.coverUrl, kind: "cover")
                guard !Task.isCancelled, self?.key == token, feature.accepts(source) else { return }
                self?.imageView?.image = UIImage(data: bytes); self?.setNeedsLayout()
            } catch {
                if (error as? NovelError) == .imageReleaseFailed { feature.error = error.localizedDescription }
            }
        }
    }
    override func layoutSubviews() {
        super.layoutSubviews()
        imageView?.frame = CGRect(x: 8, y: 8, width: 52, height: bounds.height - 16)
        textLabel?.frame.origin.x = 72; textLabel?.frame.size.width = max(0, bounds.width - 110)
        detailTextLabel?.frame.origin.x = 72; detailTextLabel?.frame.size.width = max(0, bounds.width - 110)
    }
    deinit { pending?.cancel() }
}

@MainActor
public final class NovelSearchResultsContent: UIViewController, SearchResultContent, UITableViewDataSource, UITableViewDelegate {
    public var controller: UIViewController { self }
    public var onSelect: ((NovelBook) -> Void)?
    public let session: NovelSearchSession
    private let table = UITableView(frame: .zero, style: .plain)
    private let more = UIButton(type: .system), cancelButton = UIButton(type: .system), sources = UIButton(type: .system)
    private var theme: PageTheme
    private var subscription: AnyCancellable?
    private var active = false
    private var rows: [NovelSearchBook] { session.books }
    public init(feature: NovelFeature, theme: PageTheme, matchingBook: NovelBook? = nil) {
        session = NovelSearchSession(feature: feature, matchingBook: matchingBook); self.theme = theme
        super.init(nibName: nil, bundle: nil)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    public override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.search.results"; view.backgroundColor = .clear
        table.backgroundColor = .clear; table.dataSource = self; table.delegate = self; table.rowHeight = 96; table.separatorStyle = .none
        table.register(NovelBookCell.self, forCellReuseIdentifier: "book")
        more.setTitle("加载更多", for: .normal); more.accessibilityIdentifier = "novel.search.more"
        cancelButton.setTitle("取消", for: .normal); sources.setTitle("书源", for: .normal); sources.showsMenuAsPrimaryAction = true
        more.addAction(UIAction { [weak self] _ in self?.active = true; self?.session.loadMore() }, for: .touchUpInside)
        cancelButton.addAction(UIAction { [weak self] _ in self?.cancel() }, for: .touchUpInside)
        let footer = UIStackView(arrangedSubviews: [sources, cancelButton, more]); footer.distribution = .fillEqually
        let stack = UIStackView(arrangedSubviews: [table, footer]); stack.axis = .vertical
        view.addSubview(stack); stack.pinEdges(to: view)
        footer.heightAnchor.constraint(equalToConstant: 48).isActive = true
        subscription = session.objectWillChange.sink { [weak self] in
            Task { @MainActor [weak self] in self?.render() }
        }
        render()
    }
    public func submit(_ query: String) { active = true; session.submit(query); if isViewLoaded { render() } }
    public override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        if !session.query.isEmpty { submit(session.query) }
    }
    public override func viewDidDisappear(_ animated: Bool) { super.viewDidDisappear(animated); cancel() }
    public func cancel() { active = false; session.cancel(); if isViewLoaded { for cell in table.visibleCells { (cell as? NovelBookCell)?.cancelImage() } } }
    public func close() { session.cancel(); Task { await session.close() } }
    public func applyTheme(_ theme: PageTheme) { self.theme = theme; if isViewLoaded { table.reloadData() } }
    private func render() {
        table.reloadData(); more.isEnabled = !session.loading && session.canLoadMore; more.isHidden = !session.canLoadMore
        cancelButton.isHidden = !session.loading
        sources.menu = UIMenu(children: session.states.map { item in
            let status: String
            switch item.status { case .idle: status = "未开始"; case .loading: status = "加载中"; case .loaded: status = "第\(item.page)页";
            case .exhausted: status = "已加载全部"; case .cancelled: status = "已取消"; case .failed(let error): status = error }
            return UIAction(title: item.source.name, subtitle: status) { [weak self] _ in self?.active = true; self?.session.loadMore() }
        })
        let label = UILabel(); label.textAlignment = .center; label.textColor = UIColor(theme.muted)
        label.text = session.loading ? "搜索中" : session.states.isEmpty ? "没有可搜索书源" : "未找到书籍"
        table.backgroundView = rows.isEmpty ? label : nil
        if let error = session.error, presentError(error) { session.error = nil }
    }
    private func presentError(_ error: String) -> Bool {
        var ancestor = parent
        while let page = ancestor { if let page = page as? PageViewController { return page.showError(error) }; ancestor = page.parent }
        return false
    }
    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { rows.count }
    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let cell = tableView.dequeueReusableCell(withIdentifier: "book", for: indexPath) as! NovelBookCell
        let row = rows[indexPath.row]
        cell.configure(row.book, subtitle: "\(row.candidates.count) 个来源", feature: session.feature, theme: theme, loadCover: active && view.window != nil); return cell
    }
    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true); onSelect?(rows[indexPath.row].book)
    }
    public func tableView(_ tableView: UITableView, contextMenuConfigurationForRowAt indexPath: IndexPath, point: CGPoint) -> UIContextMenuConfiguration? {
        let row = rows[indexPath.row]
        return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
            UIMenu(children: row.candidates.map { candidate in
                UIAction(title: self?.session.feature.sources.first { $0.id == candidate.installationId }?.name ?? candidate.installationId) { [weak self] _ in self?.onSelect?(candidate) }
            })
        }
    }
    public func tableView(_ tableView: UITableView, didEndDisplaying cell: UITableViewCell, forRowAt indexPath: IndexPath) { (cell as? NovelBookCell)?.cancelImage() }
}
#endif
