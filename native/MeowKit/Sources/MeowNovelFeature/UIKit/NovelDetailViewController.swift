#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class NovelDetailViewController: PageViewController {
    public var onRead: ((NovelBook) -> Void)?
    private let feature: NovelFeature
    private var book: NovelBook
    private let titleLabel = UILabel(), author = UILabel(), intro = UILabel(), latest = UILabel()
    private let shelf = UIButton(type: .system), read = UIButton(type: .system)
    private var task: Task<Void, Never>?
    private var metadataLoaded = false
    private var pendingError: String?
    public init(book: NovelBook, feature: NovelFeature, themeProvider: any PageThemeProviding) {
        self.book = book; self.feature = feature; super.init(themeProvider: themeProvider)
    }
    public override func viewDidLoad() {
        pageTitle = "书籍详情"; super.viewDidLoad(); view.accessibilityIdentifier = "novel.detail"
        titleLabel.font = .preferredFont(forTextStyle: .title1); titleLabel.numberOfLines = 0
        author.font = .preferredFont(forTextStyle: .subheadline)
        intro.numberOfLines = 0; intro.font = .preferredFont(forTextStyle: .body)
        latest.font = .preferredFont(forTextStyle: .footnote); latest.numberOfLines = 0
        read.setTitle("开始阅读", for: .normal); read.accessibilityIdentifier = "novel.detail.read"
        read.addAction(UIAction { [weak self] _ in guard let self else { return }; onRead?(book) }, for: .touchUpInside)
        shelf.accessibilityIdentifier = "novel.detail.shelf"
        shelf.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            runPageTask { [weak self] in
                guard let self else { return }
                do { try await feature.setShelf(book, present: !onShelf); render() } catch { showError(error.localizedDescription) }
            }
        }, for: .touchUpInside)
        let buttons = UIStackView(arrangedSubviews: [shelf, read]); buttons.distribution = .fillEqually
        let stack = UIStackView(arrangedSubviews: [titleLabel, author, latest, buttons, intro]); stack.axis = .vertical; stack.spacing = 20
        let scroll = UIScrollView(); view.addSubview(scroll); scroll.translatesAutoresizingMaskIntoConstraints = false
        scroll.addSubview(stack); stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            scroll.topAnchor.constraint(equalTo: contentTopAnchor), scroll.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            scroll.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor), scroll.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            stack.topAnchor.constraint(equalTo: scroll.contentLayoutGuide.topAnchor, constant: 24),
            stack.bottomAnchor.constraint(equalTo: scroll.contentLayoutGuide.bottomAnchor, constant: -24),
            stack.leadingAnchor.constraint(equalTo: scroll.contentLayoutGuide.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: scroll.contentLayoutGuide.trailingAnchor, constant: -24),
            stack.widthAnchor.constraint(equalTo: scroll.frameLayoutGuide.widthAnchor, constant: -48),
            buttons.heightAnchor.constraint(equalToConstant: 48)])
        observe(feature) { [weak self] in self?.render() }; render()
    }
    public override func pageDidBecomeVisible() {
        guard !metadataLoaded else { return }
        task?.cancel()
        task = Task { [weak self] in
            guard let self else { return }
            do {
                let source = try feature.source(for: book)
                let result = try await feature.sourcesClient.detail(book, source: source)
                guard !Task.isCancelled, feature.accepts(source) else { return }
                book = result; metadataLoaded = true; render()
            } catch { if !Task.isCancelled { pendingError = error.localizedDescription; render() } }
        }
    }
    public override func pageDidFinishAppearing() { render() }
    public override func pageDidBecomeInactive() { task?.cancel() }
    public override func pageDidBecomeHidden() {
        if navigationController?.topViewController !== self || UIApplication.shared.applicationState != .active { task?.cancel() }
    }
    public override func applyTheme() { super.applyTheme(); if isViewLoaded { render() } }
    private var onShelf: Bool { feature.local.entries.contains { ($0.id == book.id || $0.book.installationId == book.installationId && $0.book.bookUrl == book.bookUrl) && $0.onShelf } }
    private func render() {
        titleLabel.text = book.name; author.text = book.author; intro.text = book.intro; latest.text = book.latestChapterTitle
        shelf.setTitle(onShelf ? "移出书架" : "加入书架", for: .normal)
        for label in [titleLabel, intro] { label.textColor = UIColor(theme.ink) }
        for label in [author, latest] { label.textColor = UIColor(theme.muted) }
        if let pendingError, showError(pendingError) { self.pendingError = nil }
    }
}

@MainActor
final class NovelChapterPicker: PageViewController, UITableViewDataSource, UITableViewDelegate {
    private let chapters: [NovelChapter]
    private let selected: Int
    private let choose: (Int) -> Void
    private let refresh: (() -> Void)?
    var onCancel: (() -> Void)?
    private var chosen = false
    private var reversed = false
    private let table = UITableView(frame: .zero, style: .plain)
    init(chapters: [NovelChapter], selected: Int, title: String, themeProvider: any PageThemeProviding,
         refresh: (() -> Void)? = nil, choose: @escaping (Int) -> Void) {
        self.chapters = chapters; self.selected = selected; self.choose = choose; self.refresh = refresh
        super.init(themeProvider: themeProvider); pageTitle = title
    }
    override func viewDidLoad() {
        super.viewDidLoad(); table.backgroundColor = .clear; table.dataSource = self; table.delegate = self
        table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 52
        table.accessibilityIdentifier = "novel.catalog"
        view.addSubview(table); table.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([table.topAnchor.constraint(equalTo: contentTopAnchor), table.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            table.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor), table.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor)])
        let order = PageHeaderTextButton(title: "倒序") { [weak self] in self?.reversed.toggle(); self?.table.reloadData(); self?.scrollToCurrent() }
        var buttons: [UIButton] = [order]
        if let refresh { buttons.append(PageHeaderTextButton(title: "刷新") { [weak self] in refresh(); self?.navigationController?.popViewController(animated: true) }) }
        pageHeader.setTrailing(buttons)
    }
    override func pageDidFinishAppearing() { scrollToCurrent() }
    override func pageDidBecomeInactive() { if !chosen { onCancel?() } }
    private func scrollToCurrent() {
        guard chapters.indices.contains(selected) else { return }
        table.scrollToRow(at: IndexPath(row: reversed ? chapters.count - 1 - selected : selected, section: 0), at: .middle, animated: false)
    }
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { chapters.count }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let index = reversed ? chapters.count - 1 - indexPath.row : indexPath.row, chapter = chapters[index]
        let cell = UITableViewCell(style: .default, reuseIdentifier: nil); cell.backgroundColor = .clear
        cell.textLabel?.text = chapter.name
        cell.textLabel?.numberOfLines = 0; cell.textLabel?.textColor = UIColor(index == selected ? theme.accent : theme.ink)
        cell.accessoryType = index == selected ? .checkmark : .none
        if chapter.isVip && !chapter.isPay { let lock = UIImageView(image: UIImage(systemName: "lock")); lock.tintColor = UIColor(theme.muted); lock.accessibilityLabel = "付费章节"; cell.accessoryView = lock }
        return cell
    }
    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        let index = reversed ? chapters.count - 1 - indexPath.row : indexPath.row
        chosen = true; choose(index); navigationController?.popViewController(animated: true)
    }
}
#endif
