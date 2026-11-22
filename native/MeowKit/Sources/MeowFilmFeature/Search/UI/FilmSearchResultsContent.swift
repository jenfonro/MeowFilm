#if os(iOS)
import UIKit
import Combine
import MeowDebug
import MeowUI

/// The collection's displayed fields and exact selection references, not another
/// search snapshot or a native owner of aggregation and metadata.
struct FilmSearchResultsPresentation {
    struct Identifier: Hashable, Sendable {
        let displayKey: Data
    }
    struct Content: Equatable {
        let item: FilmItem
        let textBadge: String
        let label: String

        static func == (lhs: Self, rhs: Self) -> Bool {
            lhs.item.identity == rhs.item.identity && lhs.item.title == rhs.item.title &&
                lhs.item.posterURL == rhs.item.posterURL && lhs.textBadge == rhs.textBadge && lhs.label == rhs.label
        }
    }
    struct Card {
        let selection: FilmSearchCard
        let content: Content
    }
    struct Changes {
        let identifiersChanged: Bool
        let reconfigure: [Identifier]
        var isEmpty: Bool { !identifiersChanged && reconfigure.isEmpty }
    }
    private(set) var identifiers: [Identifier] = []
    private(set) var cards: [Identifier: Card] = [:]
    // Share the current input array so state-only snapshots need no display mapping.
    private var inputCards: [FilmSearchCard] = []

    mutating func update(_ visible: [FilmSearchCard],
                         imageURL: (String) -> URL?) -> Changes {
        if inputCards == visible {
            return Changes(identifiersChanged: false, reconfigure: [])
        }
        var nextIDs: [Identifier] = [], nextCards: [Identifier: Card] = [:], changed: [Identifier] = []
        nextIDs.reserveCapacity(visible.count); nextCards.reserveCapacity(visible.count)
        for card in visible {
            let id = Identifier(displayKey: Data(card.businessID.utf8))
            let previous = cards[id]
            let content: Content
            if let previous, previous.selection == card { content = previous.content }
            else { content = Content(item: card.filmItem(imageURL: imageURL), textBadge: card.textBadge, label: card.label) }
            if let previous, previous.content != content { changed.append(id) }
            nextIDs.append(id)
            nextCards[id] = Card(selection: card, content: content)
        }
        let changes = Changes(identifiersChanged: identifiers != nextIDs, reconfigure: changed)
        identifiers = nextIDs; cards = nextCards
        inputCards = visible
        return changes
    }
}

/// Content for the existing search pager. It has no header or navigation shell.
@MainActor
public final class FilmSearchResultsContent: UIViewController, SearchResultContent, UICollectionViewDelegate {
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
    var presentation = FilmSearchResultsPresentation()
    private var shownSessionError: UUID?
    private var shownMetadataError: (sessionID: UUID, state: FilmSearchChannelState)?
    private var offsets: [Bool: CGPoint] = [:]
    private var dataSource: UICollectionViewDiffableDataSource<Int, FilmSearchResultsPresentation.Identifier>!
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
        rawSwitch.control.accessibilityIdentifier = "film.search.mode"
        rawSwitch.control.accessibilityLabel = "原始列表"
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
        collection.register(FilmPosterCell.self, forCellWithReuseIdentifier: FilmPosterCell.reuseIdentifier)
        dataSource = UICollectionViewDiffableDataSource(collectionView: collection) { [weak self] view, path, key in
            guard let self, let content = self.presentation.cards[key]?.content else { return nil }
            let cell = view.dequeueReusableCell(withReuseIdentifier: FilmPosterCell.reuseIdentifier, for: path)
            (cell as? FilmPosterCell)?.configure(item: content.item,
                images: self.images, theme: self.theme, textBadge: content.textBadge, label: content.label)
            return cell
        }
        var initial = NSDiffableDataSourceSnapshot<Int, FilmSearchResultsPresentation.Identifier>()
        initial.appendSections([0])
        dataSource.apply(initial, animatingDifferences: false)
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
                shownSessionError = nil
                shownMetadataError = nil
            }
            observation = next.$snapshot.receive(on: RunLoop.main).sink { [weak self, weak next] _ in
                guard let self, let next, self.session === next else { return }
                self.render()
            }
            errorObservation = next.$error.receive(on: RunLoop.main).sink { [weak self, weak next] _ in
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
        if let session { service.release(session) }
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
        var snapshot = dataSource.snapshot()
        snapshot.reconfigureItems(snapshot.itemIdentifiers)
        dataSource.apply(snapshot, animatingDifferences: false)
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
        let changes = presentation.update(visible, imageURL: session.imageURL)
        if !changes.isEmpty {
            var snapshot: NSDiffableDataSourceSnapshot<Int, FilmSearchResultsPresentation.Identifier>
            if changes.identifiersChanged {
                snapshot = .init()
                snapshot.appendSections([0]); snapshot.appendItems(presentation.identifiers)
            } else { snapshot = dataSource.snapshot() }
            snapshot.reconfigureItems(changes.reconfigure)
            let mode = raw
            dataSource.apply(snapshot, animatingDifferences: false) { [weak self, weak session] in
                guard let self, let session, self.session === session, self.raw == mode else { return }
                self.restoreOffset(restoringOffset)
            }
        } else { restoreOffset(restoringOffset) }
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
            if shownSessionError != state.sessionID {
                let message = session?.error?.localizedDescription ?? "影视搜索失败"
                let retry: (@MainActor () -> Void)? = { [weak self] in
                    guard let self else { return }
                    self.shownSessionError = nil
                    if self.session?.snapshot.hasMore == true {
                        self.session?.loadMore()
                    } else if let query = self.session?.snapshot.query, !query.isEmpty {
                        self.submit(query)
                    }
                }
                if presentError(message, retry: retry) { shownSessionError = state.sessionID }
            }
            return
        }
        switch state.metadataState {
        case .failed:
            if shownMetadataError?.sessionID != state.sessionID || shownMetadataError?.state != .failed {
                let retry: (@MainActor () -> Void)?
                if state.hasMore {
                    retry = { [weak self] in
                        guard let self else { return }
                        self.shownMetadataError = nil
                        self.session?.loadMore()
                    }
                } else {
                    retry = nil
                }
                if presentError("TMDB 请求失败", retry: retry) {
                    shownMetadataError = (state.sessionID, .failed)
                }
            }
        case .unavailable:
            if shownMetadataError?.sessionID != state.sessionID || shownMetadataError?.state != .unavailable {
                if presentError("TMDB 不可用") {
                    shownMetadataError = (state.sessionID, .unavailable)
                }
            }
        default:
            shownMetadataError = nil
        }
    }

    @discardableResult
    private func presentError(_ message: String, retry: (@MainActor () -> Void)? = nil) -> Bool {
        var ancestor = parent
        while let page = ancestor {
            if let page = page as? PageViewController {
                page.enqueueError(message, retry: retry)
                return true
            }
            ancestor = page.parent
        }
        return false
    }

    private func makeLayout() -> UICollectionViewLayout {
        UICollectionViewCompositionalLayout { _, environment in
            let width = max(1, (environment.container.effectiveContentSize.width - 32 - 20) / 3)
            let item = NSCollectionLayoutItem(layoutSize: .init(widthDimension: .absolute(width), heightDimension: .fractionalHeight(1)))
            let group = NSCollectionLayoutGroup.horizontal(layoutSize: .init(widthDimension: .fractionalWidth(1),
                heightDimension: .absolute(width * 1.5 + 59)), repeatingSubitem: item, count: 3)
            group.interItemSpacing = .fixed(FilmPosterMetrics.spacing)
            let section = NSCollectionLayoutSection(group: group)
            section.interGroupSpacing = 16
            section.contentInsets = .init(top: 4, leading: FilmPosterMetrics.inset, bottom: 16, trailing: FilmPosterMetrics.inset)
            return section
        }
    }

    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        guard let id = dataSource.itemIdentifier(for: indexPath), let card = presentation.cards[id]?.selection,
              let session else { return }
        collectionView.deselectItem(at: indexPath, animated: false)
        onSelect?(session.context(for: card))
    }
    public func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        guard let poster = cell as? FilmPosterCell else { return }
        poster.resetPressFeedback()
        poster.cancelPendingLoad()
    }
    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        for cell in collection.visibleCells.compactMap({ $0 as? FilmPosterCell }) { cell.resetPressFeedback() }
    }
    private func cancelVisibleArtworks() {
        for cell in collection.visibleCells.compactMap({ $0 as? FilmPosterCell }) { cell.cancelPendingLoad() }
    }
    private func resumeVisibleArtworks() {
        for cell in collection.visibleCells.compactMap({ $0 as? FilmPosterCell }) { cell.resumePendingLoad() }
    }
}
#endif
