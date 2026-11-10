#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class FilmHomeViewController: PageViewController, UICollectionViewDelegate {
    public var onSelectCategory: ((FilmCategoryModel) -> Void)?
    public var onSelectItem: ((FilmItem) -> Void)?
    public var onSelectPlaybackItem: ((FilmItem, FilmCategory?) -> Void)?
    private let model: FilmHomeModel
    private let images: ArtworkImages
    private let activity = UIActivityIndicatorView(style: .medium)
    private let status = UILabel()
    private let stateView = UIStackView()
    private var sections: [FilmHomeSection] = []
    private var items: [Row: FilmItem] = [:]
    private var shownError: FilmHomeError?
    private var renderedSourceID: String?
    private struct Row: Hashable {
        let section: String
        let item: FilmItemIdentity?
    }
    private var dataSource: UICollectionViewDiffableDataSource<String, Row>!
    private lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: makeLayout())

    public init(model: FilmHomeModel, images: ArtworkImages, themeProvider: any PageThemeProviding) {
        self.model = model
        self.images = images
        super.init(themeProvider: themeProvider)
        pageTitle = "影视"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.home"
        collection.backgroundColor = .clear
        collection.accessibilityIdentifier = "film.home.sections"
        collection.delegate = self
        collection.alwaysBounceVertical = true
        collection.delaysContentTouches = false
        collection.register(FilmPosterCell.self, forCellWithReuseIdentifier: FilmPosterCell.reuseIdentifier)
        collection.register(FilmEmptyCell.self, forCellWithReuseIdentifier: FilmEmptyCell.reuseIdentifier)
        collection.register(FilmSectionHeader.self,
                            forSupplementaryViewOfKind: UICollectionView.elementKindSectionHeader,
                            withReuseIdentifier: FilmSectionHeader.reuseIdentifier)
        dataSource = UICollectionViewDiffableDataSource(collectionView: collection) { [weak self] view, path, row in
            guard let self else { return nil }
            guard let item = self.items[row] else {
                let cell = view.dequeueReusableCell(withReuseIdentifier: FilmEmptyCell.reuseIdentifier, for: path)
                if let cell = cell as? FilmEmptyCell {
                    cell.label.text = "暂无影片"
                    cell.label.textColor = UIColor(self.theme.muted)
                }
                return cell
            }
            let cell = view.dequeueReusableCell(withReuseIdentifier: FilmPosterCell.reuseIdentifier, for: path)
            (cell as? FilmPosterCell)?.configure(item: item, images: self.images, theme: self.theme)
            return cell
        }
        dataSource.supplementaryViewProvider = { [weak self] view, kind, path in
            guard let self, self.sections.indices.contains(path.section) else { return nil }
            let header = view.dequeueReusableSupplementaryView(ofKind: kind,
                withReuseIdentifier: FilmSectionHeader.reuseIdentifier, for: path)
            self.configureHeader(header, section: self.sections[path.section])
            return header
        }
        collection.refreshControl = UIRefreshControl()
        collection.refreshControl?.addAction(UIAction { [weak self] _ in self?.model.refresh() },
                                            for: .valueChanged)
        view.addSubview(collection)
        collection.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            collection.topAnchor.constraint(equalTo: contentTopAnchor),
            collection.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            collection.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            collection.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        stateView.axis = .vertical
        stateView.alignment = .center
        stateView.spacing = 12
        status.font = .systemFont(ofSize: 15)
        for item in [activity, status] { stateView.addArrangedSubview(item) }
        view.addSubview(stateView)
        stateView.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stateView.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            stateView.centerYAnchor.constraint(equalTo: view.safeAreaLayoutGuide.centerYAnchor),
        ])
        observe(model) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeActive() {
        model.loadIfNeeded()
        render()
    }

    public override func pageDidBecomeInactive() { model.close() }
    public override func pageDidBecomeHidden() { resetPressFeedback() }
    public override func pageDidFinishAppearing() { presentError() }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        status.textColor = UIColor(theme.muted)
        activity.color = UIColor(theme.muted)
        guard dataSource != nil else { return }
        var snapshot = dataSource.snapshot()
        snapshot.reconfigureItems(snapshot.itemIdentifiers)
        dataSource.apply(snapshot, animatingDifferences: false) { [weak self] in self?.updateVisibleHeaders() }
    }

    private func updateVisibleHeaders() {
        let identifiers = dataSource.snapshot().sectionIdentifiers
        for path in collection.indexPathsForVisibleSupplementaryElements(ofKind: UICollectionView.elementKindSectionHeader) {
            guard identifiers.indices.contains(path.section),
                  let section = sections.first(where: { $0.id == identifiers[path.section] }),
                  let header = collection.supplementaryView(
                    forElementKind: UICollectionView.elementKindSectionHeader, at: path) else { continue }
            configureHeader(header, section: section)
        }
    }

    private func configureHeader(_ header: UICollectionReusableView, section: FilmHomeSection) {
        let action: (() -> Void)? = model.supportsCategory(sectionID: section.id) ? { [weak self] in
            guard let self, let category = self.model.categoryModel(sectionID: section.id) else { return }
            self.onSelectCategory?(category)
        } : nil
        (header as? FilmSectionHeader)?.configure(title: section.title, theme: theme,
                                                 sectionID: section.id, onMore: action)
    }

    private func render() {
        guard isViewLoaded, dataSource != nil else { return }
        if sections != model.sections || renderedSourceID != model.selectedSourceID {
            let switchedSource = renderedSourceID != model.selectedSourceID
            renderedSourceID = model.selectedSourceID
            sections = model.sections
            items.removeAll(keepingCapacity: true)
            var snapshot = NSDiffableDataSourceSnapshot<String, Row>()
            for section in sections {
                snapshot.appendSections([section.id])
                let rows = section.items.map { item -> Row in
                    let row = Row(section: section.id, item: item.identity)
                    items[row] = item
                    return row
                }
                snapshot.appendItems(rows.isEmpty ? [Row(section: section.id, item: nil)] : rows, toSection: section.id)
            }
            snapshot.reconfigureItems(snapshot.itemIdentifiers.filter {
                dataSource.snapshot().indexOfItem($0) != nil
            })
            dataSource.apply(snapshot, animatingDifferences: false) { [weak self] in self?.updateVisibleHeaders() }
            collection.collectionViewLayout.invalidateLayout()
            if switchedSource { collection.setContentOffset(.zero, animated: false) }
        }
        stateView.isHidden = !sections.isEmpty
        status.text = model.isLoading ? nil : model.error == nil ? "暂无影片" : "加载失败"
        status.isHidden = model.isLoading
        if model.isLoading { activity.startAnimating() }
        else {
            activity.stopAnimating()
            collection.refreshControl?.endRefreshing()
        }
        if model.error == nil { shownError = nil }
        presentError()
    }

    private func presentError() {
        guard let error = model.error, shownError != error else { return }
        if showRetryableError(error.localizedDescription, retry: { [weak self] in
            self?.model.refresh()
        }) { shownError = error }
    }

    private func makeLayout() -> UICollectionViewLayout {
        UICollectionViewCompositionalLayout { [weak self] index, environment in
            let empty = self?.sections.indices.contains(index) != true || self?.sections[index].items.isEmpty == true
            let metrics = FilmPosterMetrics(viewportWidth: environment.container.effectiveContentSize.width)
            let item = NSCollectionLayoutItem(layoutSize: .init(widthDimension: .fractionalWidth(1),
                                                               heightDimension: .fractionalHeight(1)))
            let group = NSCollectionLayoutGroup.horizontal(layoutSize: .init(
                widthDimension: empty ? .fractionalWidth(1) : .absolute(metrics.width),
                heightDimension: .absolute(empty ? 42 : metrics.height)), subitems: [item])
            let section = NSCollectionLayoutSection(group: group)
            section.orthogonalScrollingBehavior = empty ? .none : .continuous
            section.interGroupSpacing = FilmPosterMetrics.spacing
            section.contentInsets = .init(top: 2, leading: FilmPosterMetrics.inset, bottom: 20,
                                          trailing: FilmPosterMetrics.inset)
            let heading = NSCollectionLayoutBoundarySupplementaryItem(layoutSize: .init(
                widthDimension: .fractionalWidth(1), heightDimension: .absolute(44)),
                elementKind: UICollectionView.elementKindSectionHeader, alignment: .top)
            section.boundarySupplementaryItems = [heading]
            return section
        }
    }

    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        collectionView.deselectItem(at: indexPath, animated: false)
        guard let row = dataSource.itemIdentifier(for: indexPath), let item = items[row] else { return }
        if let onSelectPlaybackItem {
            onSelectPlaybackItem(item, model.categoryModel(sectionID: row.section)?.category)
        } else { onSelectItem?(item) }
    }

    public func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        (cell as? FilmPosterCell)?.resetPressFeedback()
    }

    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) { resetPressFeedback() }

    private func resetPressFeedback() {
        for cell in collection.visibleCells.compactMap({ $0 as? FilmPosterCell }) { cell.resetPressFeedback() }
    }
}
#endif
