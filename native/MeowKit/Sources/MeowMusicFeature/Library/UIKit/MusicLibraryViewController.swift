#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicLibraryViewController: MusicPageViewController, UICollectionViewDataSource,
    UICollectionViewDelegateFlowLayout
{
    private let model: MusicViewModel
    private weak var router: (any MusicUIRouting)?
    private lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: UICollectionViewFlowLayout())
    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore, router: any MusicUIRouting) {
        self.model = model; self.router = router
        super.init(appearance: appearance)
    }
    public override func viewDidLoad() {
        super.viewDidLoad()
        collection.backgroundColor = .clear
        collection.dataSource = self; collection.delegate = self
        collection.register(MusicLibraryCell.self, forCellWithReuseIdentifier: "collection")
        collection.refreshControl = UIRefreshControl()
        collection.refreshControl?.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.runPageTask {
                await self.model.load()
                self.collection.refreshControl?.endRefreshing()
            }
        }, for: .valueChanged)
        view.addSubview(collection); collection.pinEdges(to: view)
        observe(model) { [weak self] in self?.collection.reloadData() }
        observe(appearance) { [weak self] in self?.collection.collectionViewLayout.invalidateLayout() }
    }
    public override func pageDidBecomeActive() {
        collection.reloadData()
    }
    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded { collection.reloadData(); collection.collectionViewLayout.invalidateLayout() }
    }
    public func collectionView(_ collectionView: UICollectionView, numberOfItemsInSection section: Int) -> Int {
        model.collections.count
    }
    public func collectionView(_ collectionView: UICollectionView, cellForItemAt indexPath: IndexPath)
        -> UICollectionViewCell {
        let item = model.collections[indexPath.item]
        let cell = collectionView.dequeueReusableCell(withReuseIdentifier: "collection", for: indexPath) as! MusicLibraryCell
        cell.configure(item, grid: appearance.preferences.playlistView == .grid, theme: theme)
        return cell
    }
    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        router?.showLocalCollection(model.collections[indexPath.item])
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
        UIEdgeInsets(top: 12, left: 16, bottom: 12, right: 16)
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
