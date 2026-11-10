#if os(iOS)
import UIKit
import Combine
import MeowUI

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
    private let modes = UISegmentedControl(items: ["聚合", "原始列表"])
    private let more = UIButton(type: .system), cancelButton = UIButton(type: .system)
    private let activity = UIActivityIndicatorView(style: .medium)
    private let empty = UILabel(), metadataStatus = UILabel()
    private var raw = false
    private var cards: [Data: FilmSearchCard] = [:]
    private var shownSessionError: UUID?
    private var shownMetadataError: UUID?
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
        modes.selectedSegmentIndex = 0
        modes.accessibilityIdentifier = "film.search.mode"
        modes.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.offsets[self.raw] = self.collection.contentOffset
            self.raw = self.modes.selectedSegmentIndex == 1
            self.render(restoringOffset: self.offsets[self.raw] ?? .zero)
        }, for: .valueChanged)
        metadataStatus.font = .systemFont(ofSize: 12)
        metadataStatus.accessibilityIdentifier = "film.search.metadata.status"
        collection.backgroundColor = .clear
        collection.alwaysBounceVertical = true
        collection.delaysContentTouches = false
        collection.delegate = self
        collection.accessibilityIdentifier = "film.search.grid"
        collection.register(FilmPosterCell.self, forCellWithReuseIdentifier: FilmPosterCell.reuseIdentifier)
        dataSource = UICollectionViewDiffableDataSource(collectionView: collection) { [weak self] view, path, key in
            guard let self, let card = self.cards[key], let session = self.session else { return nil }
            let cell = view.dequeueReusableCell(withReuseIdentifier: FilmPosterCell.reuseIdentifier, for: path)
            (cell as? FilmPosterCell)?.configure(item: card.filmItem(imageURL: session.imageURL),
                images: self.images, theme: self.theme, textBadge: card.textBadge, label: card.label)
            return cell
        }
        empty.font = .systemFont(ofSize: 15)
        empty.textAlignment = .center
        collection.backgroundView = empty
        more.accessibilityIdentifier = "film.search.more"
        more.setTitle("加载更多", for: .normal)
        more.addAction(UIAction { [weak self] _ in self?.session?.loadMore() }, for: .touchUpInside)
        cancelButton.accessibilityIdentifier = "film.search.cancel"
        cancelButton.setTitle("取消", for: .normal)
        cancelButton.addAction(UIAction { [weak self] _ in self?.session?.cancel() }, for: .touchUpInside)
        let controls = UIStackView(arrangedSubviews: [modes, metadataStatus])
        controls.axis = .horizontal; controls.alignment = .center; controls.spacing = 12
        let footer = UIStackView(arrangedSubviews: [activity, cancelButton, more])
        footer.axis = .horizontal; footer.alignment = .center; footer.distribution = .equalCentering
        for child in [controls, collection, footer] {
            view.addSubview(child); child.translatesAutoresizingMaskIntoConstraints = false
        }
        NSLayoutConstraint.activate([
            controls.topAnchor.constraint(equalTo: view.topAnchor, constant: 4),
            controls.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 16),
            controls.trailingAnchor.constraint(lessThanOrEqualTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -16),
            controls.heightAnchor.constraint(equalToConstant: 36),
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
        do {
            let next = try service.session(for: query, reusing: session)
            guard session !== next || observation == nil else { return }
            let changed = session !== next
            observation?.cancel()
            errorObservation?.cancel()
            session = next
            if changed {
                offsets.removeAll()
                shownSessionError = nil
                shownMetadataError = nil
            }
            observation = next.$snapshot.receive(on: RunLoop.main).sink { [weak self] _ in self?.render() }
            errorObservation = next.$error.receive(on: RunLoop.main).sink { [weak self] _ in self?.render() }
            if changed, isViewLoaded { collection.setContentOffset(.zero, animated: false) }
            render()
        } catch { presentError("影视搜索不可用") }
    }

    public func cancel() { session?.cancel() }
    public func close() {
        if let session { service.release(session) }
        observation?.cancel(); observation = nil
        errorObservation?.cancel(); errorObservation = nil
    }
    public func applyTheme(_ theme: PageTheme) {
        self.theme = theme
        guard isViewLoaded, dataSource != nil else { return }
        empty.textColor = UIColor(theme.muted)
        metadataStatus.textColor = UIColor(theme.muted)
        more.tintColor = UIColor(theme.accent); cancelButton.tintColor = UIColor(theme.ink)
        activity.color = UIColor(theme.muted)
        modes.selectedSegmentTintColor = UIColor(theme.chip)
        var snapshot = dataSource.snapshot()
        snapshot.reconfigureItems(snapshot.itemIdentifiers)
        dataSource.apply(snapshot, animatingDifferences: false)
    }

    public override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        render()
    }

    private func render(restoringOffset: CGPoint? = nil) {
        guard isViewLoaded, dataSource != nil, let state = session?.snapshot else { return }
        let visible = raw ? state.rawCards : state.cards
        cards = Dictionary(uniqueKeysWithValues: visible.map { (Data($0.displayKey.utf8), $0) })
        var snapshot = NSDiffableDataSourceSnapshot<Int, Data>()
        snapshot.appendSections([0]); snapshot.appendItems(visible.map { Data($0.displayKey.utf8) })
        let previous = Set(dataSource.snapshot().itemIdentifiers)
        snapshot.reconfigureItems(snapshot.itemIdentifiers.filter { previous.contains($0) })
        dataSource.apply(snapshot, animatingDifferences: false) { [weak self] in
            guard let self, let restoringOffset else { return }
            self.collection.layoutIfNeeded()
            self.collection.setContentOffset(restoringOffset, animated: false)
        }
        modes.isHidden = state.siteItems.isEmpty
        empty.isHidden = !visible.isEmpty || state.loading || state.failed || session?.error != nil
        let cancelled = state.siteState == .cancelled || state.metadataState == .cancelled
        empty.text = cancelled ? "已取消" : state.noSources ? "尚未启用可搜索来源" : "未找到影片"
        more.isHidden = !state.hasMore
        more.isEnabled = !state.loading && state.hasMore
        cancelButton.isHidden = !state.loading
        if state.loading { activity.startAnimating() } else { activity.stopAnimating() }
        switch state.metadataState {
        case .unavailable: metadataStatus.text = "TMDB 不可用"
        default: metadataStatus.text = nil
        }
        if state.failed || session?.error != nil, shownSessionError != state.sessionID {
            if presentError(session?.error?.localizedDescription ?? "影视搜索失败") { shownSessionError = state.sessionID }
        } else if state.metadataState == .failed, shownMetadataError != state.sessionID {
            if presentError("TMDB 搜索失败") { shownMetadataError = state.sessionID }
        }
    }

    @discardableResult
    private func presentError(_ message: String) -> Bool {
        var ancestor = parent
        while let page = ancestor {
            if let page = page as? PageViewController { return page.showError(message) }
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
        guard let id = dataSource.itemIdentifier(for: indexPath), let card = cards[id], let session else { return }
        collectionView.deselectItem(at: indexPath, animated: false)
        onSelect?(session.context(for: card))
    }
    public func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) { (cell as? FilmPosterCell)?.resetPressFeedback() }
    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        for cell in collection.visibleCells.compactMap({ $0 as? FilmPosterCell }) { cell.resetPressFeedback() }
    }
}
#endif
