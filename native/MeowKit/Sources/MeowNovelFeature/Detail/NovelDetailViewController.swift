#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class NovelDetailViewController: PageViewController {
    public var onRead: ((NovelBook) -> Void)?
    let feature: NovelFeature
    var book: NovelBook
    private let titleLabel = UILabel(), author = UILabel(), intro = UILabel(), latest = UILabel()
    private let shelf = UIButton(type: .system), read = UIButton(type: .system)
    private var task: Task<Void, Never>?
    private var metadataLoaded = false
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
                do { try await feature.setShelf(book, present: !onShelf); render() }
                catch is CancellationError { }
                catch { if !Task.isCancelled { enqueueError(error.localizedDescription) } }
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
        observe(feature, whenVisible: true) { [weak self] in self?.render() }; render()
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
            } catch { if !Task.isCancelled { enqueueError(error.localizedDescription) } }
        }
    }
    public override func pageDidBecomeInactive() { task?.cancel() }
    public override func pageDidBecomeHidden() {
        if navigationController?.topViewController !== self || UIApplication.shared.applicationState != .active { task?.cancel() }
    }
    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        for label in [titleLabel, intro] { label.textColor = UIColor(theme.ink) }
        for label in [author, latest] { label.textColor = UIColor(theme.muted) }
    }
    private var onShelf: Bool { feature.local.entries.contains { ($0.id == book.id || $0.book.installationId == book.installationId && $0.book.bookUrl == book.bookUrl) && $0.onShelf } }
    private func render() {
        titleLabel.text = book.name; author.text = book.author; intro.text = book.intro; latest.text = book.latestChapterTitle
        shelf.setTitle(onShelf ? "移出书架" : "加入书架", for: .normal)
    }
}

@MainActor
final class NovelChapterPicker: PageViewController, UITableViewDelegate {
    private let chapters: [NovelChapter]
    private let selected: Int
    private let choose: (Int) -> Void
    private let refresh: (() -> Void)?
    var onCancel: (() -> Void)?
    private var chosen = false
    private var reversed = false
    private let table = UITableView(frame: .zero, style: .plain)
    private var dataSource: UITableViewDiffableDataSource<Int, Int>!

    init(chapters: [NovelChapter], selected: Int, title: String, themeProvider: any PageThemeProviding,
         refresh: (() -> Void)? = nil, choose: @escaping (Int) -> Void) {
        self.chapters = chapters; self.selected = selected; self.choose = choose; self.refresh = refresh
        super.init(themeProvider: themeProvider); pageTitle = title
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.delegate = self
        table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 52
        table.accessibilityIdentifier = "novel.catalog"
        table.register(NovelChapterCell.self, forCellReuseIdentifier: NovelChapterCell.reuseIdentifier)
        view.addSubview(table); table.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([table.topAnchor.constraint(equalTo: contentTopAnchor), table.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            table.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor), table.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor)])
        dataSource = UITableViewDiffableDataSource<Int, Int>(tableView: table) { [weak self] tableView, indexPath, chapterIndex in
            let cell = tableView.dequeueReusableCell(withIdentifier: NovelChapterCell.reuseIdentifier, for: indexPath) as! NovelChapterCell
            if let self, self.chapters.indices.contains(chapterIndex) {
                cell.configure(chapter: self.chapters[chapterIndex], selected: chapterIndex == self.selected, theme: self.theme)
            }
            return cell
        }
        dataSource.defaultRowAnimation = .none
        let order = PageHeaderTextButton(title: "倒序") { [weak self] in
            guard let self else { return }
            self.reversed.toggle()
            self.applyChapterSnapshot()
            self.scrollToCurrent()
        }
        var buttons: [UIButton] = [order]
        if let refresh { buttons.append(PageHeaderTextButton(title: "刷新") { [weak self] in refresh(); self?.navigationController?.popViewController(animated: true) }) }
        pageHeader.setTrailing(buttons)
        applyChapterSnapshot()
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, dataSource != nil else { return }
        for indexPath in table.indexPathsForVisibleRows ?? [] {
            guard let chapterIndex = dataSource.itemIdentifier(for: indexPath),
                  chapters.indices.contains(chapterIndex),
                  let cell = table.cellForRow(at: indexPath) as? NovelChapterCell else { continue }
            cell.configure(chapter: chapters[chapterIndex], selected: chapterIndex == selected, theme: theme)
        }
    }

    override func pageDidFinishAppearing() { scrollToCurrent() }
    override func pageDidBecomeInactive() { if !chosen { onCancel?() } }

    private func applyChapterSnapshot() {
        var snapshot = NSDiffableDataSourceSnapshot<Int, Int>()
        snapshot.appendSections([0])
        let indices = Array(chapters.indices)
        snapshot.appendItems(reversed ? indices.reversed() : indices, toSection: 0)
        dataSource.apply(snapshot, animatingDifferences: false)
    }

    private func scrollToCurrent() {
        guard chapters.indices.contains(selected), let indexPath = dataSource.indexPath(for: selected) else { return }
        table.scrollToRow(at: indexPath, at: .middle, animated: false)
    }

    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        guard let index = dataSource.itemIdentifier(for: indexPath) else { return }
        chosen = true; choose(index); navigationController?.popViewController(animated: true)
    }
}

@MainActor
private final class NovelChapterCell: UITableViewCell {
    static let reuseIdentifier = "novel.chapter"
    private let lockIcon = UIImageView(image: UIImage(systemName: "lock"))

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: .default, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        lockIcon.accessibilityLabel = "付费章节"
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        accessoryType = .none
        accessoryView = nil
    }

    func configure(chapter: NovelChapter, selected: Bool, theme: PageTheme) {
        var content = UIListContentConfiguration.cell()
        content.text = chapter.name
        content.textProperties.font = .preferredFont(forTextStyle: .body)
        content.textProperties.adjustsFontForContentSizeCategory = true
        content.textProperties.numberOfLines = 0
        content.textProperties.color = UIColor(selected ? theme.accent : theme.ink)
        contentConfiguration = content
        if chapter.isVip && !chapter.isPay {
            lockIcon.tintColor = UIColor(theme.muted)
            accessoryType = .none
            accessoryView = lockIcon
        } else {
            accessoryView = nil
            accessoryType = selected ? .checkmark : .none
        }
    }
}
#endif
