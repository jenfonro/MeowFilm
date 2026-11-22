#if os(iOS)
import UIKit
import Combine
import MeowDebug
import MeowUI

@MainActor
public final class MusicLibraryViewController: MusicPageViewController, UICollectionViewDelegateFlowLayout
{
    let model: MusicViewModel
    weak var router: (any MusicUIRouting)?
    private lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: UICollectionViewFlowLayout())
    private lazy var heartbeat = MusicLibraryHeartbeatButton { [weak self] in
        guard let self, self.isPageActive else { return }
        let account = self.model.searchAccessGeneration
        self.runPageTask { [weak self] in
            guard let self, self.model.searchAccessGeneration == account else { return }
            await self.model.startHeartbeat()
        }
    }
    private var dataSource: UICollectionViewDiffableDataSource<Int, Data>!
    private var items: [Data: MusicCollection] = [:]
    private let emptyLabel = UILabel()
    private var access: UUID?
    private var request = UUID()
    var loading = false
    var loadError: String?
    private var shownLoadError: String?
    private var renderedPlaylistAppearance: MusicPlaylistAppearance?
    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore, router: any MusicUIRouting) {
        self.model = model; self.router = router
        super.init(appearance: appearance)
    }
    public override func viewDidLoad() {
        super.viewDidLoad()
        heartbeat.accessibilityIdentifier = "music.library.heartbeat"
        view.addSubview(heartbeat)
        heartbeat.translatesAutoresizingMaskIntoConstraints = false
        collection.backgroundColor = .clear
        collection.delegate = self
        collection.alwaysBounceVertical = true
        collection.register(MusicLibraryCell.self, forCellWithReuseIdentifier: "collection")
        collection.register(MusicLibrarySectionHeader.self, forSupplementaryViewOfKind: UICollectionView.elementKindSectionHeader,
                            withReuseIdentifier: "section")
        dataSource = UICollectionViewDiffableDataSource(collectionView: collection) { [weak self] view, path, id in
            guard let self, let item = self.items[id] else { return nil }
            let cell = view.dequeueReusableCell(withReuseIdentifier: "collection", for: path) as! MusicLibraryCell
            cell.configure(item, grid: self.appearance.preferences.playlistView == .grid, theme: self.theme, images: self.model.artworkImages)
            return cell
        }
        dataSource.supplementaryViewProvider = { [weak self] view, kind, path in
            guard let self, kind == UICollectionView.elementKindSectionHeader else { return nil }
            let header = view.dequeueReusableSupplementaryView(ofKind: kind, withReuseIdentifier: "section", for: path) as! MusicLibrarySectionHeader
            let section = self.dataSource.snapshot().sectionIdentifiers[path.section]
            header.configure(title: section == 0 ? "我的歌单" : "网络歌单", theme: self.theme)
            return header
        }
        collection.refreshControl = UIRefreshControl()
        collection.refreshControl?.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.loadLibrary()
        }, for: .valueChanged)
        view.addSubview(collection)
        collection.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            collection.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            collection.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            heartbeat.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            heartbeat.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16),
            heartbeat.topAnchor.constraint(equalTo: contentTopAnchor, constant: PageLayoutMetrics.contentGap),
            heartbeat.heightAnchor.constraint(equalToConstant: 48),
            collection.topAnchor.constraint(equalTo: heartbeat.bottomAnchor, constant: 8),
            collection.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        emptyLabel.textAlignment = .center
        observe(model.libraryChanges, whenVisible: true) { [weak self] in self?.render() }
        observe(model.accessChanges) { [weak self] in self?.reconcileAccess() }
        observe(model.playbackChanges, whenVisible: true) { [weak self] in self?.renderHeartbeat() }
        observeDistinct(appearance.$preferences.map(\.playlistView).eraseToAnyPublisher(), immediateOnMain: false, whenVisible: true) {
            [weak self] _ in
            self?.updatePlaylistLayout()
        }
        render()
    }
    public override func pageDidBecomeActive() {
        reconcileAccess()
    }
    public override func pageDidBecomeInactive() {
        request = UUID()
        cancelVisibleArtworkLoads()
    }
    public override func pageDidBecomeHidden() {
        cancelVisibleArtworkLoads()
    }
    public override func pageDidBecomeVisible() {
        renderState()
        for cell in collection.visibleCells.compactMap({ $0 as? MusicLibraryCell }) {
            cell.resumePendingArtworkLoad()
        }
        model.artworkImages.retryFailedVisible()
    }
    private func cancelVisibleArtworkLoads() {
        for cell in collection.visibleCells.compactMap({ $0 as? MusicLibraryCell }) {
            cell.cancelPendingArtworkLoad()
        }
    }
    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded {
            emptyLabel.textColor = UIColor(theme.muted)
            collection.refreshControl?.tintColor = UIColor(theme.accent)
            renderHeartbeat()
            updateVisibleCells()
            for case let header as MusicLibrarySectionHeader in collection.visibleSupplementaryViews(ofKind: UICollectionView.elementKindSectionHeader) {
                header.applyTheme(theme)
            }
        }
    }
    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        guard let id = dataSource.itemIdentifier(for: indexPath), let item = items[id] else { return }
        if let remote = item.remoteCollection { router?.showPublicCollection(remote) }
        else { router?.showLocalCollection(item) }
    }
    public func collectionView(_ collectionView: UICollectionView, contextMenuConfigurationForItemAt indexPath: IndexPath,
                               point: CGPoint) -> UIContextMenuConfiguration? {
        guard let id = dataSource.itemIdentifier(for: indexPath), let item = items[id], item.networkReference != nil else { return nil }
        return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
            UIMenu(children: [UIAction(title: "取消收藏", image: UIImage(systemName: "star.slash"), attributes: .destructive) {
                [weak self] _ in
                guard let self, self.isPageActive, self.items[id] == item else { return }
                self.presentPageConfirmation(title: "取消收藏歌单", confirmTitle: "取消收藏", destructive: true, onConfirm: { [weak self] in
                    guard let self, self.items[id] == item else { return }
                    let model = self.model
                    self.runPageTask { await model.removeNetworkPlaylist(item) }
                })
            }])
        }
    }
    public func collectionView(_ collectionView: UICollectionView, willDisplay cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
    }
    public func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
                               sizeForItemAt indexPath: IndexPath) -> CGSize {
        if appearance.preferences.playlistView == .list {
            return CGSize(width: max(0, collectionView.bounds.width - 32), height: 68)
        }
        let count: CGFloat = collectionView.bounds.width < 359 ? 2 : 3
        let width = max(0, (collectionView.bounds.width - 32 - (count - 1) * 12) / count)
        return CGSize(width: width, height: width + 54)
    }
    public func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
                               insetForSectionAt section: Int) -> UIEdgeInsets {
        UIEdgeInsets(top: PageLayoutMetrics.contentGap, left: 16, bottom: 12, right: 16)
    }
    public func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
                               referenceSizeForHeaderInSection section: Int) -> CGSize {
        CGSize(width: collectionView.bounds.width, height: 30)
    }
    public func requestCreatePlaylist() {
        presentPageInputAlert(
            title: "新建歌单",
            fields: [PageAlertTextField(placeholder: "歌单名称")],
            confirmTitle: "创建",
            onConfirm: { [weak self] values in
                guard let self else { return }
                let name = (values.first ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                guard !name.isEmpty else { return }
                self.runPageTask { await self.model.createPlaylist(name) }
            }
        )
    }

    public func togglePlaylistAppearance() {
        let next: MusicPlaylistAppearance = appearance.preferences.playlistView == .grid ? .list : .grid
        let appearance = self.appearance
        Task { [weak self] in
            if !(await appearance.set(\.playlistView, next)), let error = appearance.persistenceError {
                self?.enqueueError(error)
            }
        }
    }

    private func updatePlaylistLayout() {
        guard renderedPlaylistAppearance != appearance.preferences.playlistView else { return }
        renderedPlaylistAppearance = appearance.preferences.playlistView
        let anchor = collection.indexPathsForVisibleItems.compactMap { path -> (IndexPath, CGRect)? in
            collection.layoutAttributesForItem(at: path).map { (path, $0.frame) }
        }.min { left, right in
            left.1.minY == right.1.minY ? left.1.minX < right.1.minX : left.1.minY < right.1.minY
        }
        let relativeY = anchor.map { $0.1.minY - collection.contentOffset.y }
        collection.collectionViewLayout.invalidateLayout()
        collection.layoutIfNeeded()
        if let anchor, let relativeY, let frame = collection.layoutAttributesForItem(at: anchor.0)?.frame {
            let top = -collection.adjustedContentInset.top
            let bottom = max(top, collection.contentSize.height - collection.bounds.height + collection.adjustedContentInset.bottom)
            collection.setContentOffset(CGPoint(x: collection.contentOffset.x,
                y: min(bottom, max(top, frame.minY - relativeY))), animated: false)
        }
        updateVisibleCells()
    }

    private func reconcileAccess() {
        guard isPageActive else { return }
        guard model.isAvailable, !model.isClosed else {
            access = nil; request = UUID(); loading = false; loadError = nil
            collection.refreshControl?.endRefreshing()
            render()
            return
        }
        guard access != model.accountAccessGeneration else { return }
        access = model.accountAccessGeneration
        request = UUID(); loading = false; loadError = nil
        render()
        if !model.hasLoadedLibrary, !model.isLoading { loadLibrary(refresh: false) }
    }

    func loadLibrary(refresh: Bool = true) {
        guard isPageActive, model.isAvailable, !model.isClosed, !loading, !model.isLoading else {
            collection.refreshControl?.endRefreshing()
            return
        }
        if refresh { model.artworkImages.retryFailedVisible() }
        let own = UUID(), access = model.accountAccessGeneration
        request = own; loading = true; loadError = nil; shownLoadError = nil
        renderState()
        DebugRuntime.log(.musicLibrary, sub: "library", event: "load.start", level: .debug, data: .object([
            "refresh": .bool(refresh)
        ]))
        let model = model
        runPageTask { [weak self, model] in
            guard !Task.isCancelled, self?.request == own else { return }
            model.error = nil
            if refresh { await model.load() }
            else { await model.ensureLibraryLoaded() }
            guard !Task.isCancelled, let self, self.request == own, model.accountAccessGeneration == access else { return }
            self.loading = false; self.loadError = self.model.notices.error
            self.collection.refreshControl?.endRefreshing()
            self.render()
            DebugRuntime.log(.musicLibrary, sub: "library", event: "load.end", level: .debug, data: .object([
                "refresh": .bool(refresh),
                "collections": .int(self.model.collections.count),
                "error": .optional(self.loadError)
            ]))
        }
    }

    private func render() {
        guard dataSource != nil else { return }
        renderHeartbeat()
        let values = model.isAvailable && !model.isClosed ? model.collections : []
        let previous = items
        items = Dictionary(uniqueKeysWithValues: values.map { (Data($0.id.utf8), $0) })
        var snapshot = NSDiffableDataSourceSnapshot<Int, Data>()
        let ids = values.map { Data($0.id.utf8) }
        for section in [0, 1] {
            let group = values.filter { ($0.networkReference == nil ? 0 : 1) == section }.map { Data($0.id.utf8) }
            if !group.isEmpty { snapshot.appendSections([section]); snapshot.appendItems(group, toSection: section) }
        }
        let changed = ids.filter { previous[$0] != nil && previous[$0] != items[$0] }
        snapshot.reconfigureItems(changed)
        if dataSource.snapshot().sectionIdentifiers != snapshot.sectionIdentifiers ||
            dataSource.snapshot().itemIdentifiers != snapshot.itemIdentifiers || !changed.isEmpty {
            dataSource.apply(snapshot, animatingDifferences: false)
        }
        renderState()
    }

    private func renderHeartbeat() {
        heartbeat.isEnabled = model.isAvailable && !model.isClosed && !model.isChangingSimilarMode &&
            !model.isChangingQueueRecommendations && !model.isChangingQueue && !model.isNavigating
        heartbeat.applyTheme(theme)
    }

    private func updateVisibleCells() {
        guard dataSource != nil else { return }
        for path in collection.indexPathsForVisibleItems {
            guard let id = dataSource.itemIdentifier(for: path), let item = items[id],
                  let cell = collection.cellForItem(at: path) as? MusicLibraryCell else { continue }
            cell.configure(item, grid: appearance.preferences.playlistView == .grid, theme: theme, images: model.artworkImages)
        }
    }

    private func renderState() {
        guard isViewLoaded else { return }
        let text = items.isEmpty ? loading || model.isLoading ? "加载中" :
            model.hasLoadedLibrary ? "暂无歌单" : nil : nil
        if let text {
            emptyLabel.text = text
            emptyLabel.textColor = UIColor(theme.muted)
            collection.backgroundView = emptyLabel
        } else { collection.backgroundView = nil }
        guard let loadError, shownLoadError != loadError else { return }
        let own = request
        shownLoadError = loadError
        enqueueError(loadError, retry: { [weak self] in
            guard let self, self.request == own else { return }
            self.shownLoadError = nil
            self.loadLibrary()
        })
    }
}

@MainActor
private final class MusicLibraryCell: UICollectionViewCell {
    private let artwork = MusicArtworkView(size: 0)
    private let placeholderIcon = UIImageView()
    private let title = UILabel(), count = UILabel()
    private var grid = true
    override init(frame: CGRect) {
        super.init(frame: frame)
        artwork.layer.cornerRadius = 8
        artwork.layer.borderWidth = 0.5
        artwork.onImage = { [weak self] _, _ in self?.placeholderIcon.isHidden = true }
        placeholderIcon.contentMode = .scaleAspectFit
        placeholderIcon.isUserInteractionEnabled = false
        placeholderIcon.isAccessibilityElement = false
        title.numberOfLines = 2
        for item in [artwork, placeholderIcon, title, count] { contentView.addSubview(item) }
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func prepareForReuse() {
        super.prepareForReuse()
        artwork.cancelPendingArtworkLoad()
        placeholderIcon.isHidden = true
    }
    func cancelPendingArtworkLoad() {
        artwork.cancelPendingArtworkLoad()
    }
    func resumePendingArtworkLoad() {
        artwork.resumePendingLoadIfNeeded()
        placeholderIcon.isHidden = artwork.image != nil
    }
    func configure(_ item: MusicCollection, grid: Bool, theme: PageTheme, images: MusicArtworkImages) {
        self.grid = grid
        artwork.backgroundColor = UIColor(theme.chip)
        artwork.layer.borderColor = UIColor(theme.border).cgColor
        placeholderIcon.tintColor = UIColor(theme.muted)
        placeholderIcon.image = item.kind == "favorites"
            ? PageIcon.image(.heart, filled: false, size: grid ? 28 : 20)
            : UIImage(systemName: "music.note.list",
                      withConfiguration: UIImage.SymbolConfiguration(pointSize: grid ? 24 : 18, weight: .regular))
        artwork.load(item.artwork, images: images)
        placeholderIcon.isHidden = artwork.image != nil
        title.text = item.title
        count.text = item.networkReference.map { $0.platform.title } ?? "\(item.count)首"
        title.font = .systemFont(ofSize: grid ? 13 : 16)
        count.font = .systemFont(ofSize: 11)
        title.textColor = UIColor(theme.ink); count.textColor = UIColor(theme.muted)
        accessibilityIdentifier = "music.library.collection.\(item.id)"
        setNeedsLayout()
    }
    override func layoutSubviews() {
        super.layoutSubviews()
        if grid {
            artwork.frame = CGRect(x: 0, y: 0, width: bounds.width, height: bounds.width)
            title.frame = CGRect(x: 0, y: bounds.width + 4, width: bounds.width, height: 32)
            count.frame = CGRect(x: 0, y: bounds.width + 38, width: bounds.width, height: 14)
        } else {
            artwork.frame = CGRect(x: 0, y: 10, width: 48, height: 48)
            title.frame = CGRect(x: 60, y: 10, width: max(0, bounds.width - 60), height: 28)
            count.frame = CGRect(x: 60, y: 40, width: max(0, bounds.width - 60), height: 16)
        }
        let iconSide: CGFloat = grid ? 32 : 22
        placeholderIcon.frame = CGRect(
            x: artwork.frame.midX - iconSide / 2,
            y: artwork.frame.midY - iconSide / 2,
            width: iconSide,
            height: iconSide
        )
    }
}

@MainActor
private final class MusicLibraryHeartbeatButton: UIButton {
    private let iconView = UIImageView(image: MusicPlaybackModeIcon.image(.heartbeat))
    private let titleCopy = UILabel()
    private let chevron = UIImageView(image: UIImage(
        systemName: "chevron.right",
        withConfiguration: UIImage.SymbolConfiguration(pointSize: 13, weight: .regular)
    ))
    private lazy var pressFeedback = PressFeedback(view: self)
    private var currentTheme = PageTheme()

    init(action: @escaping @MainActor () -> Void) {
        super.init(frame: .zero)
        layer.cornerRadius = 12
        layer.cornerCurve = .continuous
        layer.borderWidth = 1
        clipsToBounds = true
        accessibilityLabel = "云喜欢心动"
        accessibilityTraits = .button

        iconView.contentMode = .scaleAspectFit
        iconView.isAccessibilityElement = false
        iconView.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            iconView.widthAnchor.constraint(equalToConstant: 20),
            iconView.heightAnchor.constraint(equalToConstant: 20),
        ])

        titleCopy.text = "云喜欢心动"
        titleCopy.font = .systemFont(ofSize: 15, weight: .regular)
        titleCopy.isAccessibilityElement = false

        chevron.contentMode = .scaleAspectFit
        chevron.isAccessibilityElement = false

        let spacer = UIView()
        spacer.isUserInteractionEnabled = false

        let row = UIStackView(arrangedSubviews: [iconView, titleCopy, spacer, chevron])
        row.axis = .horizontal
        row.alignment = .center
        row.spacing = 10
        row.isUserInteractionEnabled = false
        addSubview(row)
        row.pinEdges(to: self, insets: UIEdgeInsets(top: 0, left: 14, bottom: 0, right: 14))
        addAction(UIAction { _ in action() }, for: .touchUpInside)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var isHighlighted: Bool {
        didSet {
            pressFeedback.setPressed(isHighlighted)
            backgroundColor = UIColor(isHighlighted ? currentTheme.chip : currentTheme.surface)
        }
    }

    override var isEnabled: Bool {
        didSet {
            alpha = isEnabled ? 1 : 0.45
            accessibilityTraits = isEnabled ? .button : [.button, .notEnabled]
        }
    }

    func applyTheme(_ theme: PageTheme) {
        currentTheme = theme
        backgroundColor = UIColor(isHighlighted ? theme.chip : theme.surface)
        layer.borderColor = UIColor(theme.border).cgColor
        iconView.tintColor = UIColor(theme.accent)
        titleCopy.textColor = UIColor(theme.ink)
        chevron.tintColor = UIColor(theme.muted)
    }
}

@MainActor
private final class MusicLibrarySectionHeader: UICollectionReusableView {
    private let title = UILabel()
    override init(frame: CGRect) {
        super.init(frame: frame)
        title.font = .systemFont(ofSize: 14)
        addSubview(title)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    func configure(title: String, theme: PageTheme) {
        self.title.text = title; applyTheme(theme)
        accessibilityIdentifier = title == "网络歌单" ? "music.library.network" : "music.library.managed"
    }
    func applyTheme(_ theme: PageTheme) { title.textColor = UIColor(theme.muted) }
    override func layoutSubviews() {
        super.layoutSubviews()
        title.frame = CGRect(x: 16, y: 4, width: max(0, bounds.width - 32), height: 22)
    }
}
#endif
