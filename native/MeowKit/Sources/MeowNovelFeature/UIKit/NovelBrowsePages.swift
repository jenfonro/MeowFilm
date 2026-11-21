#if os(iOS)
import UIKit
import Combine
import MeowDebug
import MeowUI

@MainActor
public final class NovelHomeViewController: PageViewController, UITableViewDataSource, UITableViewDelegate {
    public var onSelect: ((NovelBook) -> Void)?
    public let discovery: NovelDiscoverySession
    private lazy var channels = FilterTagsView(themeProvider: themeProvider, identifier: "novel.channel")
    private lazy var kinds = FilterTagsView(themeProvider: themeProvider, identifier: "novel.kind")
    private let backdrop = UIButton(type: .custom)
    private let table = UITableView(frame: .zero, style: .plain)
    private let empty = UILabel()
    private let more = UIButton(type: .system)
    private let activity = UIActivityIndicatorView(style: .medium)
    private var channelsHeight: NSLayoutConstraint!
    private var kindsHeight: NSLayoutConstraint!
    private var kindsTop: NSLayoutConstraint!
    private var tableTop: NSLayoutConstraint!
    private var layoutWidth: CGFloat = -1
    var renderedBooks: [NovelBook] = []
    private var renderedTarget = ""
    private var shownError: String?

    public init(feature: NovelFeature, themeProvider: any PageThemeProviding) {
        discovery = NovelDiscoverySession(feature: feature)
        super.init(themeProvider: themeProvider)
    }
    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "novel.home"
        table.dataSource = self; table.delegate = self
        table.backgroundColor = .clear; table.separatorStyle = .none
        table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 130
        table.keyboardDismissMode = .onDrag
        table.accessibilityIdentifier = "novel.books"
        table.register(NovelBookCell.self, forCellReuseIdentifier: NovelBookCell.reuseIdentifier)
        table.refreshControl = UIRefreshControl()
        table.refreshControl?.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            guard isPageVisible else { table.refreshControl?.endRefreshing(); return }
            discovery.refresh()
        }, for: .valueChanged)
        empty.font = .preferredFont(forTextStyle: .body); empty.adjustsFontForContentSizeCategory = true
        empty.textAlignment = .center; empty.numberOfLines = 0
        empty.accessibilityIdentifier = "novel.books.empty"
        more.setTitle("加载更多", for: .normal); more.titleLabel?.font = .systemFont(ofSize: 14)
        more.accessibilityIdentifier = "novel.explore.more"
        more.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            if discovery.error != nil { shownError = nil; presentErrors() }
            else { discovery.loadMore() }
        }, for: .touchUpInside)
        more.addSubview(activity); activity.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([activity.centerXAnchor.constraint(equalTo: more.centerXAnchor),
                                     activity.centerYAnchor.constraint(equalTo: more.centerYAnchor)])
        backdrop.backgroundColor = UIColor(PageTheme.scrim)
        backdrop.accessibilityLabel = "收起标签"; backdrop.accessibilityIdentifier = "novel.tags.dismiss"
        backdrop.isHidden = true
        backdrop.addAction(UIAction { [weak self] _ in self?.collapseTags() }, for: .touchUpInside)
        channels.onExpansionChanged = { [weak self] in
            guard let self else { return }
            if channels.isExpanded { kinds.collapse(); view.bringSubviewToFront(channels) }
            updateTagsLayout()
        }
        kinds.onExpansionChanged = { [weak self] in
            guard let self else { return }
            if kinds.isExpanded { channels.collapse(); view.bringSubviewToFront(kinds) }
            updateTagsLayout()
        }
        for child in [table, backdrop, kinds, channels] {
            view.addSubview(child); child.translatesAutoresizingMaskIntoConstraints = false
        }
        channelsHeight = channels.heightAnchor.constraint(equalToConstant: 0)
        kindsHeight = kinds.heightAnchor.constraint(equalToConstant: 0)
        kindsTop = kinds.topAnchor.constraint(equalTo: contentTopAnchor)
        tableTop = table.topAnchor.constraint(equalTo: contentTopAnchor)
        NSLayoutConstraint.activate([
            channels.topAnchor.constraint(equalTo: contentTopAnchor), channelsHeight,
            channels.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            channels.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            kindsTop, kindsHeight,
            kinds.leadingAnchor.constraint(equalTo: channels.leadingAnchor), kinds.trailingAnchor.constraint(equalTo: channels.trailingAnchor),
            tableTop, table.leadingAnchor.constraint(equalTo: channels.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: channels.trailingAnchor), table.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            backdrop.topAnchor.constraint(equalTo: contentTopAnchor), backdrop.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            backdrop.leadingAnchor.constraint(equalTo: view.leadingAnchor), backdrop.trailingAnchor.constraint(equalTo: view.trailingAnchor),
        ])
        observe(discovery) { [weak self] in self?.render() }
        observe(discovery.feature) { [weak self] in self?.presentErrors() }
        render()
    }
    public override func pageDidBecomeVisible() { discovery.activate(); render(reloadBooks: true) }
    public override func pageDidFinishAppearing() { presentErrors() }
    public override func pageDidBecomeHidden() { discovery.deactivate(); collapseTags(); cancelCovers() }
    public override func pageDidBecomeInactive() {
        discovery.deactivate(); collapseTags(); cancelCovers()
        Task { [discovery] in await discovery.close() }
    }
    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded, tableTop != nil { render(reloadBooks: true) }
    }
    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        if abs(channels.bounds.width - layoutWidth) > 0.5 { updateTagsLayout() }
        if table.tableFooterView != nil, more.frame.width != table.bounds.width {
            more.frame.size.width = table.bounds.width; table.tableFooterView = more
        }
    }
    private func render(reloadBooks: Bool = false) {
        guard isViewLoaded, tableTop != nil else { return }
        channels.configure(filters: discovery.channels.map { FilterTag(id: $0.id, title: $0.title) },
                           selectedID: discovery.selectedChannelID ?? "", theme: theme) { [weak self] id in
            self?.discovery.selectChannel(id: id)
        }
        kinds.configure(filters: (discovery.selectedChannel?.items ?? []).map { FilterTag(id: $0.id, title: $0.title) },
                        selectedID: discovery.selectedKindID ?? "", theme: theme) { [weak self] id in
            self?.discovery.selectKind(id: id)
        }
        updateTagsLayout()
        let target = (discovery.selectedSource?.id ?? "") + "\n" + (discovery.selectedKind?.url ?? "")
        let changedTarget = target != renderedTarget
        renderedTarget = target
        if reloadBooks || renderedBooks != discovery.books {
            renderedBooks = discovery.books; table.reloadData()
        }
        if changedTarget { table.setContentOffset(.zero, animated: false) }
        empty.textColor = UIColor(theme.muted)
        empty.text = discovery.loading ? "加载中" : discovery.selectedSource == nil ? "没有可发现书源" : "暂无书籍"
        table.backgroundView = discovery.books.isEmpty && discovery.error == nil ? empty : nil
        more.tintColor = UIColor(theme.ink); activity.color = UIColor(theme.muted)
        more.setTitle(discovery.loading ? nil : discovery.error == nil ? "加载更多" : "重试", for: .normal)
        more.isEnabled = !discovery.loading
        if discovery.loading { activity.startAnimating() }
        else { activity.stopAnimating(); table.refreshControl?.endRefreshing() }
        if discovery.loading || discovery.canLoadMore || discovery.error != nil {
            more.frame = CGRect(x: 0, y: 0, width: table.bounds.width, height: 44)
            if table.tableFooterView !== more { table.tableFooterView = more }
        } else { table.tableFooterView = nil }
        presentErrors()
    }
    private func updateTagsLayout() {
        guard tableTop != nil else { return }
        let width = channels.bounds.width
        layoutWidth = width
        channels.updateLayout(width: width); kinds.updateLayout(width: width)
        channelsHeight.constant = channels.preferredHeight(for: width)
        kindsHeight.constant = kinds.preferredHeight(for: width)
        kindsTop.constant = channels.collapsedHeight
        tableTop.constant = channels.collapsedHeight + kinds.collapsedHeight
        channels.isHidden = channelsHeight.constant == 0; kinds.isHidden = kindsHeight.constant == 0
        let expanded = channels.isExpanded || kinds.isExpanded
        backdrop.isHidden = !expanded
        if expanded {
            view.bringSubviewToFront(backdrop)
            view.bringSubviewToFront(channels.isExpanded ? channels : kinds)
        }
        table.accessibilityElementsHidden = expanded
        kinds.accessibilityElementsHidden = channels.isExpanded
        channels.accessibilityElementsHidden = kinds.isExpanded
    }
    private func collapseTags() { channels.collapse(); kinds.collapse() }
    private func cancelCovers() { for cell in table.visibleCells { (cell as? NovelBookCell)?.cancelImage() } }
    private func presentErrors() {
        guard isPageVisible else { return }
        if discovery.error == nil { shownError = nil }
        if let error = discovery.error, shownError != error,
           showRetryableError(error, retry: { [weak self] in self?.discovery.retry() }) { shownError = error }
        if let error = discovery.feature.error, showError(error) { discovery.feature.error = nil }
    }
    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { renderedBooks.count }
    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let cell = tableView.dequeueReusableCell(withIdentifier: NovelBookCell.reuseIdentifier, for: indexPath) as! NovelBookCell
        cell.configure(renderedBooks[indexPath.row], feature: discovery.feature, theme: theme, loadCover: isPageVisible)
        return cell
    }
    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true); onSelect?(renderedBooks[indexPath.row])
    }
    public func tableView(_ tableView: UITableView, didEndDisplaying cell: UITableViewCell, forRowAt indexPath: IndexPath) {
        (cell as? NovelBookCell)?.cancelImage()
    }
    public func scrollViewDidScroll(_ scrollView: UIScrollView) {
        guard isPageVisible, scrollView.isDragging || scrollView.isDecelerating, discovery.error == nil,
              scrollView.contentOffset.y > 0,
              scrollView.contentOffset.y + scrollView.bounds.height > scrollView.contentSize.height - 200 else { return }
        discovery.loadMore()
    }
    deinit { let discovery = discovery; Task { await discovery.close() } }
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
    var rows: [NovelSearchBook] { session.books }
    public init(feature: NovelFeature, theme: PageTheme, matchingBook: NovelBook? = nil) {
        session = NovelSearchSession(feature: feature, matchingBook: matchingBook); self.theme = theme
        super.init(nibName: nil, bundle: nil)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    public override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.search.results"; view.backgroundColor = .clear
        table.backgroundColor = .clear; table.dataSource = self; table.delegate = self; table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 130; table.separatorStyle = .none
        table.accessibilityIdentifier = "novel.search.books"
        table.register(NovelBookCell.self, forCellReuseIdentifier: NovelBookCell.reuseIdentifier)
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
    public func submit(_ query: String) {
        DebugRuntime.log(.search, sub: "novel", event: "submit", level: .debug, data: .object([
            "query": .string(query)
        ]))
        active = true; session.submit(query); if isViewLoaded { render() }
    }
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
        let cell = tableView.dequeueReusableCell(withIdentifier: NovelBookCell.reuseIdentifier, for: indexPath) as! NovelBookCell
        let row = rows[indexPath.row]
        cell.configure(row.book, sourceCount: row.candidates.count, feature: session.feature, theme: theme, loadCover: active && view.window != nil); return cell
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
