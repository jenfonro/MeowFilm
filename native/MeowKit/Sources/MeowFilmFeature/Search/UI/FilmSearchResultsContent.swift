#if os(iOS)
import UIKit
import Combine
import MeowDebug
import MeowUI

@MainActor
public final class FilmSearchResultsContent: UIViewController, SearchResultContent, UICollectionViewDelegate {
    struct CardEntry {
        let id: Data
        let selection: FilmSearchCard
        let item: FilmItem
        var textBadge: String { selection.textBadge }
        var label: String { selection.label }

        func displayChanged(from old: Self) -> Bool {
            item.identity != old.item.identity || item.title != old.item.title ||
                item.posterURL != old.item.posterURL || textBadge != old.textBadge || label != old.label
        }
    }

    public var controller: UIViewController { self }
    public var onSelect: ((FilmSearchContext) -> Void)?
    public private(set) var session: FilmSearchSession?
    private let service: FilmSearchService
    private let images: ArtworkImages
    private var theme: PageTheme
    private var observation: AnyCancellable?
    private var errorObservation: AnyCancellable?
    private let rawSwitch = SettingsSwitch(), rawLabel = UILabel()
    private let more = UIButton(type: .system), cancelButton = UIButton(type: .system)
    private let activity = UIActivityIndicatorView(style: .medium)
    private let empty = UILabel()
    private var raw = false
    private(set) var projection = IndexedDiffableProjection<Int, Data, CardEntry>()
    private(set) var lastInputCards: [FilmSearchCard] = []
    private var offsets: [Bool: CGPoint] = [:]
    private var dataSource: UICollectionViewDiffableDataSource<Int, Data>!
    private lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: makeLayout())

    public init(service: FilmSearchService, images: ArtworkImages, theme: PageTheme) {
        self.service = service; self.images = images; self.theme = theme
        super.init(nibName: nil, bundle: nil)
    }
    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear
        view.accessibilityIdentifier = "film.search.results"
        rawSwitch.accessibilityIdentifier = "film.search.mode"
        rawSwitch.accessibilityLabel = "原始列表"
        rawSwitch.changed = { [weak self] isOn in
            guard let self else { return }
            self.offsets[self.raw] = self.collection.contentOffset
            self.raw = isOn
            self.render(restoringOffset: self.offsets[self.raw] ?? .zero)
        }
        rawLabel.text = "原始列表"
        rawLabel.font = .preferredFont(forTextStyle: .subheadline)
        rawLabel.adjustsFontForContentSizeCategory = true
        rawLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
        rawSwitch.setContentCompressionResistancePriority(.required, for: .horizontal)
        collection.backgroundColor = .clear
        collection.alwaysBounceVertical = true
        collection.delaysContentTouches = false
        collection.delegate = self
        collection.accessibilityIdentifier = "film.search.grid"
        collection.register(PosterCatalogCell.self, forCellWithReuseIdentifier: PosterCatalogCell.reuseIdentifier)
        dataSource = UICollectionViewDiffableDataSource(collectionView: collection) { [weak self] view, path, key in
            guard let self, let entry = self.projection[key] else { return nil }
            let cell = view.dequeueReusableCell(withReuseIdentifier: PosterCatalogCell.reuseIdentifier, for: path)
            let card = FilmHomeViewController.makePosterCard(
                entry.item,
                images: self.images,
                textBadge: entry.textBadge,
                label: entry.label
            )
            (cell as? PosterCatalogCell)?.configure(card: card, theme: self.theme)
            return cell
        }
        var initialBuilder = DiffableSnapshotBuilder<Int, Data>()
        initialBuilder.appendSection(0)
        dataSource.apply(initialBuilder.build(), animatingDifferences: false)
        empty.font = .systemFont(ofSize: 15)
        empty.textAlignment = .center
        collection.backgroundView = empty
        more.accessibilityIdentifier = "film.search.more"
        more.setTitle("加载更多", for: .normal)
        more.addAction(UIAction { [weak self] _ in self?.session?.loadMore() }, for: .touchUpInside)
        cancelButton.accessibilityIdentifier = "film.search.cancel"
        cancelButton.setTitle("取消", for: .normal)
        cancelButton.addAction(UIAction { [weak self] _ in self?.session?.cancel() }, for: .touchUpInside)
        let controls = UIStackView(arrangedSubviews: [UIView(), rawLabel, rawSwitch])
        controls.axis = .horizontal; controls.alignment = .center; controls.spacing = 12
        let footer = UIStackView(arrangedSubviews: [activity, cancelButton, more])
        footer.axis = .horizontal; footer.alignment = .center; footer.distribution = .equalCentering
        for child in [controls, collection, footer] {
            view.addSubview(child); child.translatesAutoresizingMaskIntoConstraints = false
        }
        NSLayoutConstraint.activate([
            controls.topAnchor.constraint(equalTo: view.topAnchor, constant: 4),
            controls.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 16),
            controls.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -16),
            controls.heightAnchor.constraint(greaterThanOrEqualToConstant: 44),
            collection.topAnchor.constraint(equalTo: controls.bottomAnchor, constant: 8),
            collection.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            collection.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            collection.bottomAnchor.constraint(equalTo: footer.topAnchor),
            footer.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 24),
            footer.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -24),
            footer.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
            footer.heightAnchor.constraint(equalToConstant: 44),
        ])
        applyTheme(theme)
        render()
    }

    public func submit(_ query: String) {
        DebugRuntime.log(.search, sub: "film", event: "submit", level: .debug, data: .object([
            "query": .string(query)
        ]))
        do {
            let next = try service.session(for: query, reusing: session)
            guard session !== next || observation == nil else { return }
            let changed = session !== next
            let queryChanged = session.map { !$0.snapshot.query.utf8.elementsEqual(next.snapshot.query.utf8) } ?? true
            observation?.cancel()
            errorObservation?.cancel()
            session = next
            next.setPresentationVisible(viewIfLoaded?.window != nil)
            if changed {
                if queryChanged { offsets.removeAll() }
                ancestorPage?.clearErrorSource("film.search.session")
                ancestorPage?.clearErrorSource("film.search.metadata")
            }
            observation = next.$snapshot.dropFirst().receive(on: RunLoop.main).sink { [weak self, weak next] _ in
                guard let self, let next, self.session === next else { return }
                self.render()
            }
            errorObservation = next.$error.dropFirst().receive(on: RunLoop.main).sink { [weak self, weak next] _ in
                guard let self, let next, self.session === next else { return }
                self.renderStatus()
            }
            if queryChanged, isViewLoaded { collection.setContentOffset(.zero, animated: false) }
            render()
        } catch { presentError("影视搜索不可用") }
    }

    public func cancel() {
        session?.cancel()
        if isViewLoaded { cancelVisibleArtworks() }
    }
    public func close() {
        if isViewLoaded { cancelVisibleArtworks() }
        if let session {
            self.session = nil
            service.release(session)
        }
        observation?.cancel(); observation = nil
        errorObservation?.cancel(); errorObservation = nil
    }
    public func applyTheme(_ theme: PageTheme) {
        let changed = self.theme != theme
        self.theme = theme
        guard isViewLoaded, dataSource != nil else { return }
        empty.textColor = UIColor(theme.muted)
        more.tintColor = UIColor(theme.accent); cancelButton.tintColor = UIColor(theme.ink)
        activity.color = UIColor(theme.muted)
        rawLabel.textColor = UIColor(theme.ink)
        rawSwitch.applyTheme(theme)
        guard changed else { return }
        for cell in collection.visibleCells {
            (cell as? PosterCatalogCell)?.applyTheme(theme)
        }
    }

    public override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        session?.setPresentationVisible(true)
        render()
        resumeVisibleArtworks()
    }

    public override func viewWillDisappear(_ animated: Bool) {
        session?.setPresentationVisible(false)
        cancelVisibleArtworks()
        super.viewWillDisappear(animated)
    }

    private func render(restoringOffset: CGPoint? = nil) {
        guard isViewLoaded, dataSource != nil, let session else { return }
        let state = session.snapshot
        let visible = raw ? state.rawCards : state.cards
        if lastInputCards != visible {
            lastInputCards = visible
            let entries = visible.map { card -> CardEntry in
                let key = Data(card.id.utf8)
                let item = projection[key].flatMap { $0.selection == card ? $0.item : nil } ?? card.filmItem(imageURL: session.imageURL)
                return CardEntry(id: key, selection: card, item: item)
            }
            let update = projection.update(section: 0, items: entries, id: \.id) { $1.displayChanged(from: $0) }
            if update.structureChanged || !update.changedItemIDs.isEmpty {
                let mode = raw
                dataSource.apply(update.snapshot, animatingDifferences: false) { [weak self, weak session] in
                    guard let self, let session, self.session === session, self.raw == mode else { return }
                    self.restoreOffset(restoringOffset)
                }
            } else {
                restoreOffset(restoringOffset)
            }
        } else {
            restoreOffset(restoringOffset)
        }
        renderStatus()
    }

    private func restoreOffset(_ offset: CGPoint?) {
        guard let offset else { return }
        collection.layoutIfNeeded()
        collection.setContentOffset(offset, animated: false)
    }

    private func renderStatus() {
        guard isViewLoaded, let state = session?.snapshot else { return }
        let visible = raw ? state.rawCards : state.cards
        empty.isHidden = !visible.isEmpty || state.loading || state.failed || session?.error != nil
        let cancelled = state.siteState == .cancelled || state.metadataState == .cancelled
        empty.text = cancelled ? "已取消" : state.noSources ? "尚未启用可搜索来源" : "未找到影片"
        let showPagination = state.hasMore && !(visible.isEmpty && (state.failed || state.metadataState == .failed))
        more.isHidden = !showPagination
        more.isEnabled = !state.loading && showPagination
        cancelButton.isHidden = !state.loading
        if state.loading { activity.startAnimating() } else { activity.stopAnimating() }
        if state.failed || session?.error != nil {
            let message = session?.error?.localizedDescription ?? "影视搜索失败"
            ancestorPage?.syncErrorSource(
                "film.search.session",
                message: message,
                token: AnyHashable(state.sessionID),
                retry: { [weak self] in
                    guard let self else { return }
                    if self.session?.snapshot.hasMore == true {
                        self.session?.loadMore()
                    } else if let query = self.session?.snapshot.query, !query.isEmpty {
                        self.submit(query)
                    }
                }
            )
            return
        }
        ancestorPage?.clearErrorSource("film.search.session")
        switch state.metadataState {
        case .failed:
            let retry: (@MainActor () -> Void)? = if state.hasMore {
                { [weak self] in self?.session?.loadMore() }
            } else {
                nil
            }
            ancestorPage?.syncErrorSource(
                "film.search.metadata",
                message: "TMDB 请求失败",
                token: AnyHashable("\(state.sessionID.uuidString):failed"),
                retry: retry
            )
        case .unavailable:
            ancestorPage?.syncErrorSource(
                "film.search.metadata",
                message: "TMDB 不可用",
                token: AnyHashable("\(state.sessionID.uuidString):unavailable")
            )
        default:
            ancestorPage?.clearErrorSource("film.search.metadata")
        }
    }

    private var ancestorPage: PageViewController? {
        var ancestor = parent
        while let page = ancestor {
            if let page = page as? PageViewController { return page }
            ancestor = page.parent
        }
        return nil
    }

    private func presentError(_ message: String, retry: (@MainActor () -> Void)? = nil) {
        ancestorPage?.enqueueError(message, retry: retry)
    }

    private func makeLayout() -> UICollectionViewLayout {
        UICollectionViewCompositionalLayout { _, environment in
            let width = max(1, (environment.container.effectiveContentSize.width - 32 - 20) / 3)
            let item = NSCollectionLayoutItem(layoutSize: .init(widthDimension: .absolute(width), heightDimension: .fractionalHeight(1)))
            let group = NSCollectionLayoutGroup.horizontal(layoutSize: .init(widthDimension: .fractionalWidth(1),
                heightDimension: .absolute(width * 1.5 + 59)), repeatingSubitem: item, count: 3)
            group.interItemSpacing = .fixed(PosterCatalogMetrics.spacing)
            let section = NSCollectionLayoutSection(group: group)
            section.interGroupSpacing = 16
            section.contentInsets = .init(top: 4, leading: PosterCatalogMetrics.inset, bottom: 16, trailing: PosterCatalogMetrics.inset)
            return section
        }
    }

    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        guard let id = dataSource.itemIdentifier(for: indexPath), let card = projection[id]?.selection,
              let session else { return }
        collectionView.deselectItem(at: indexPath, animated: false)
        onSelect?(session.context(for: card))
    }
    public func collectionView(_ collectionView: UICollectionView, willDisplay cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        (cell as? PosterCatalogCell)?.resumePendingLoad()
    }
    public func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        guard let poster = cell as? PosterCatalogCell else { return }
        poster.resetPressFeedback()
        poster.cancelPendingLoad()
    }
    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        for cell in collection.visibleCells.compactMap({ $0 as? PosterCatalogCell }) { cell.resetPressFeedback() }
    }
    private func cancelVisibleArtworks() {
        for cell in collection.visibleCells.compactMap({ $0 as? PosterCatalogCell }) { cell.cancelPendingLoad() }
    }
    private func resumeVisibleArtworks() {
        for cell in collection.visibleCells.compactMap({ $0 as? PosterCatalogCell }) { cell.resumePendingLoad() }
    }
}
#endif
