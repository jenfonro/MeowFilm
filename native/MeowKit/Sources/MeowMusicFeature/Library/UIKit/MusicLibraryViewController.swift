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
        collection.backgroundColor = .clear
        collection.delegate = self
        collection.alwaysBounceVertical = true
        collection.register(MusicLibraryCell.self, forCellWithReuseIdentifier: "collection")
        dataSource = UICollectionViewDiffableDataSource(collectionView: collection) { [weak self] view, path, id in
            guard let self, let item = self.items[id] else { return nil }
            let cell = view.dequeueReusableCell(withReuseIdentifier: "collection", for: path) as! MusicLibraryCell
            cell.configure(item, grid: self.appearance.preferences.playlistView == .grid, theme: self.theme)
            return cell
        }
        collection.refreshControl = UIRefreshControl()
        collection.refreshControl?.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.loadLibrary()
        }, for: .valueChanged)
        view.addSubview(collection); collection.pinEdges(to: view)
        observe(model.libraryChanges) { [weak self] in self?.render() }
        observe(model.accessChanges) { [weak self] in self?.reconcileAccess() }
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
    public override func pageDidBecomeVisible() { renderState() }
    public override func pageDidFinishAppearing() {
        if loadError != nil { renderState() }
    }
    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded {
            updateVisibleCells()
        }
    }
    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        guard let id = dataSource.itemIdentifier(for: indexPath), let item = items[id] else { return }
        router?.showLocalCollection(item)
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
        let values = model.isAvailable && !model.isClosed ? model.collections : []
        let previous = items
        items = Dictionary(uniqueKeysWithValues: values.map { (Data($0.id.utf8), $0) })
        var snapshot = NSDiffableDataSourceSnapshot<Int, Data>()
        snapshot.appendSections([0])
        let ids = values.map { Data($0.id.utf8) }
        snapshot.appendItems(ids)
        let changed = ids.filter { previous[$0] != nil && previous[$0] != items[$0] }
        snapshot.reconfigureItems(changed)
        if dataSource.snapshot().itemIdentifiers != ids || !changed.isEmpty {
            dataSource.apply(snapshot, animatingDifferences: false)
        }
        renderState()
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
        title.text = item.title; count.text = "\(item.count)首"
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
#endif
