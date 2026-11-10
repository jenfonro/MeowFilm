#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicLibraryViewController: MusicPageViewController, UICollectionViewDelegateFlowLayout
{
    private let model: MusicViewModel
    private weak var router: (any MusicUIRouting)?
    private lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: UICollectionViewFlowLayout())
    private lazy var heartbeat = PageHeaderTextButton(title: "云喜欢心动") { [weak self] in
        guard let self, self.isPageActive else { return }
        let account = self.model.searchAccessGeneration
        self.runPageTask { [weak self] in
            guard let self, self.model.searchAccessGeneration == account else { return }
            await self.model.startHeartbeat()
        }
    }
    private var dataSource: UICollectionViewDiffableDataSource<Int, Data>!
    private var items: [Data: MusicCollection] = [:]
    private var access: UUID?
    private var request = UUID()
    private var loading = false
    private var loadError: String?
    private var shownLoadError: String?
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
            cell.configure(item, grid: self.appearance.preferences.playlistView == .grid, theme: self.theme)
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
            heartbeat.topAnchor.constraint(equalTo: contentTopAnchor, constant: PageLayoutMetrics.contentGap),
            heartbeat.heightAnchor.constraint(equalToConstant: 44),
            collection.topAnchor.constraint(equalTo: heartbeat.bottomAnchor, constant: 8),
            collection.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        observe(model.libraryChanges) { [weak self] in self?.render() }
        observe(model.accessChanges) { [weak self] in self?.reconcileAccess() }
        observe(model.playbackChanges) { [weak self] in self?.renderHeartbeat() }
        observe(appearance.$preferences.map(\.playlistView).removeDuplicates().dropFirst().eraseToAnyPublisher()) {
            [weak self] _ in
            self?.collection.collectionViewLayout.invalidateLayout()
            self?.updateVisibleCells()
        }
        render()
    }
    public override func pageDidBecomeActive() {
        reconcileAccess()
    }
    public override func pageDidBecomeInactive() { request = UUID() }
    public override func pageDidBecomeVisible() {
        renderState()
        MusicArtworkView.retryFailedLoads(in: view)
    }
    public override func pageDidFinishAppearing() {
        if loadError != nil { renderState() }
    }
    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded {
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
                let alert = UIAlertController(title: "取消收藏歌单", message: nil, preferredStyle: .alert)
                alert.addAction(UIAlertAction(title: "取消", style: .cancel))
                alert.addAction(UIAlertAction(title: "取消收藏", style: .destructive) { [weak self] _ in
                    guard let self, self.items[id] == item else { return }
                    self.runPageTask { await self.model.removeNetworkPlaylist(item) }
                })
                self.present(alert, animated: true)
            }])
        }
    }
    public func collectionView(_ collectionView: UICollectionView, willDisplay cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        if isPageVisible { MusicArtworkView.retryFailedLoads(in: cell) }
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
        let alert = UIAlertController(title: "新建歌单", message: nil, preferredStyle: .alert)
        alert.addTextField { $0.placeholder = "歌单名称" }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "创建", style: .default) { [weak self, weak alert] _ in
            guard let self else { return }
            let name = alert?.textFields?.first?.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard !name.isEmpty else { return }
            self.runPageTask { await self.model.createPlaylist(name) }
        })
        present(alert, animated: true)
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

    private func loadLibrary(refresh: Bool = true) {
        guard isPageActive, model.isAvailable, !model.isClosed, !loading, !model.isLoading else {
            collection.refreshControl?.endRefreshing()
            return
        }
        if refresh { MusicArtworkView.retryFailedLoads(in: view) }
        let own = UUID(), access = model.accountAccessGeneration
        request = own; loading = true; loadError = nil; shownLoadError = nil
        renderState()
        let model = model
        runPageTask { [weak self, model] in
            guard !Task.isCancelled, self?.request == own else { return }
            model.error = nil
            if refresh { await model.load() }
            else { await model.ensureLibraryLoaded() }
            guard !Task.isCancelled, let self, self.request == own, model.accountAccessGeneration == access else { return }
            self.loading = false; self.loadError = self.model.error
            self.collection.refreshControl?.endRefreshing()
            self.render()
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
            cell.configure(item, grid: appearance.preferences.playlistView == .grid, theme: theme)
        }
    }

    private func renderState() {
        guard isViewLoaded else { return }
        let text = items.isEmpty ? loading || model.isLoading ? "加载中" :
            model.hasLoadedLibrary ? "暂无歌单" : nil : nil
        if let text {
            let label = UILabel()
            label.text = text; label.textAlignment = .center; label.textColor = UIColor(theme.muted)
            collection.backgroundView = label
        } else { collection.backgroundView = nil }
        guard canPresentPageAlert, let loadError, shownLoadError != loadError else { return }
        let own = request
        let alert = UIAlertController(title: "错误", message: loadError, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
            guard let self, self.request == own else { return }
            self.loadLibrary()
        })
        shownLoadError = loadError
        present(alert, animated: true)
    }
}

@MainActor
private final class MusicLibraryCell: UICollectionViewCell {
    private let artwork = MusicArtworkView(size: 0)
    private let title = UILabel(), count = UILabel()
    private var grid = true
    override init(frame: CGRect) {
        super.init(frame: frame)
        title.numberOfLines = 2
        for item in [artwork, title, count] { contentView.addSubview(item) }
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    func configure(_ item: MusicCollection, grid: Bool, theme: PageTheme) {
        self.grid = grid
        artwork.load(item.artwork)
        title.text = item.title
        count.text = item.networkReference.map { $0.platform.title } ?? "\(item.count)首"
        title.font = .systemFont(ofSize: grid ? 13 : 16, weight: .medium)
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
    }
}

@MainActor
private final class MusicLibrarySectionHeader: UICollectionReusableView {
    private let title = UILabel()
    override init(frame: CGRect) {
        super.init(frame: frame)
        title.font = .systemFont(ofSize: 14, weight: .medium)
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
