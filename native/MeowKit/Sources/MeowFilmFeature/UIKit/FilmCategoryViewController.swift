#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class FilmCategoryViewController: PageViewController, UICollectionViewDelegate {
    public var onSelectItem: ((FilmItem) -> Void)?
    private let model: FilmCategoryModel
    private let images: ArtworkImages
    private let filters = UIStackView()
    private lazy var emptyFiltersHeight = filters.heightAnchor.constraint(equalToConstant: 0)
    private var filterViews: [String: FilmCategoryFilterRow] = [:]
    private var filterHeights: [String: NSLayoutConstraint] = [:]
    private let more = UIButton(type: .system)
    private let activity = UIActivityIndicatorView(style: .medium)
    private let empty = UILabel()
    private var shownError: FilmHomeError?
    private var renderedItems: [FilmItem] = []
    private var items: [FilmItemIdentity: FilmItem] = [:]
    private var dataSource: UICollectionViewDiffableDataSource<Int, FilmItemIdentity>!
    private lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: makeLayout())

    public init(model: FilmCategoryModel, images: ArtworkImages, themeProvider: any PageThemeProviding) {
        self.model = model
        self.images = images
        super.init(themeProvider: themeProvider)
        pageTitle = model.category.title
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.category.\(model.category.rawValue)"
        filters.axis = .vertical
        collection.backgroundColor = .clear
        collection.alwaysBounceVertical = true
        collection.delaysContentTouches = false
        collection.delegate = self
        collection.accessibilityIdentifier = "film.category.grid"
        collection.register(FilmPosterCell.self, forCellWithReuseIdentifier: FilmPosterCell.reuseIdentifier)
        dataSource = UICollectionViewDiffableDataSource(collectionView: collection) { [weak self] view, path, id in
            guard let self, let item = self.items[id] else { return nil }
            let cell = view.dequeueReusableCell(withReuseIdentifier: FilmPosterCell.reuseIdentifier, for: path)
            (cell as? FilmPosterCell)?.configure(item: item, images: self.images, theme: self.theme)
            return cell
        }
        collection.refreshControl = UIRefreshControl()
        collection.refreshControl?.addAction(UIAction { [weak self] _ in self?.model.refresh() }, for: .valueChanged)
        activity.accessibilityIdentifier = "film.category.loading"
        more.accessibilityIdentifier = "film.category.more"
        more.titleLabel?.font = .systemFont(ofSize: 13)
        more.addAction(UIAction { [weak self] _ in self?.model.loadMore() }, for: .touchUpInside)
        empty.font = .systemFont(ofSize: 15)
        empty.textAlignment = .center
        empty.text = "暂无影片"
        collection.backgroundView = empty
        for child in [filters, collection, more, activity] {
            view.addSubview(child)
            child.translatesAutoresizingMaskIntoConstraints = false
        }
        NSLayoutConstraint.activate([
            filters.topAnchor.constraint(equalTo: contentTopAnchor),
            filters.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            filters.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            collection.topAnchor.constraint(equalTo: filters.bottomAnchor),
            collection.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            collection.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            collection.bottomAnchor.constraint(equalTo: more.topAnchor),
            more.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            more.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            more.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
            more.heightAnchor.constraint(equalToConstant: 44),
            activity.centerXAnchor.constraint(equalTo: more.centerXAnchor),
            activity.centerYAnchor.constraint(equalTo: more.centerYAnchor),
        ])
        observe(model) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeActive() { model.loadIfNeeded(); render() }
    public override func pageDidBecomeInactive() { model.close() }
    public override func pageDidBecomeHidden() { resetPressFeedback() }
    public override func pageDidFinishAppearing() { presentError() }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        updateFilterHeights()
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, dataSource != nil else { return }
        empty.textColor = UIColor(theme.muted)
        activity.color = UIColor(theme.muted)
        more.tintColor = UIColor(theme.accent)
        renderFilters()
        var snapshot = dataSource.snapshot()
        snapshot.reconfigureItems(snapshot.itemIdentifiers)
        dataSource.apply(snapshot, animatingDifferences: false)
    }

    private func render() {
        guard isViewLoaded, dataSource != nil else { return }
        renderFilters()
        if renderedItems != model.items || dataSource.snapshot().sectionIdentifiers.isEmpty {
            let reset = !renderedItems.isEmpty && model.items.isEmpty
            renderedItems = model.items
            items = Dictionary(uniqueKeysWithValues: model.items.map { ($0.identity, $0) })
            var snapshot = NSDiffableDataSourceSnapshot<Int, FilmItemIdentity>()
            snapshot.appendSections([0])
            snapshot.appendItems(model.items.map(\.identity))
            snapshot.reconfigureItems(snapshot.itemIdentifiers.filter { dataSource.snapshot().indexOfItem($0) != nil })
            dataSource.apply(snapshot, animatingDifferences: false)
            if reset { collection.setContentOffset(.zero, animated: false) }
        }
        empty.isHidden = !model.items.isEmpty || model.isLoading || model.error != nil
        more.setTitle(model.isLoading ? nil : model.hasMore ? "加载更多" : "没有更多了", for: .normal)
        more.isEnabled = !model.isLoading && model.error == nil && model.hasMore
        if model.isLoading { activity.startAnimating() }
        else { activity.stopAnimating(); collection.refreshControl?.endRefreshing() }
        if model.error == nil { shownError = nil }
        presentError()
    }

    private func renderFilters() {
        let rows = model.filterRows
        emptyFiltersHeight.isActive = rows.isEmpty
        for id in Array(filterViews.keys) where !rows.contains(where: { $0.id == id }) {
            filterViews.removeValue(forKey: id)?.removeFromSuperview()
            filterHeights.removeValue(forKey: id)
        }
        for (index, row) in rows.enumerated() {
            let rowView: FilmCategoryFilterRow
            if let existing = filterViews[row.id] { rowView = existing }
            else {
                rowView = FilmCategoryFilterRow(title: row.title, id: row.id, themeProvider: themeProvider)
                filterViews[row.id] = rowView
                filters.insertArrangedSubview(rowView, at: index)
                let height = rowView.heightAnchor.constraint(equalToConstant: rowView.tags.collapsedHeight)
                height.isActive = true
                filterHeights[row.id] = height
                rowView.tags.onExpansionChanged = { [weak self] in self?.updateFilterHeights() }
            }
            rowView.label.textColor = UIColor(theme.muted)
            rowView.tags.configure(filters: row.options, selectedID: model.filters[row.id] ?? "", theme: theme) { [weak self] value in
                guard let self else { return }
                self.model.selectFilter(row.id, value: value)
                self.collection.setContentOffset(.zero, animated: false)
            }
        }
        updateFilterHeights()
    }

    private func updateFilterHeights() {
        for (id, row) in filterViews {
            let width = max(0, filters.bounds.width - FilmCategoryFilterRow.labelWidth)
            row.tags.updateLayout(width: width)
            let height = row.tags.preferredHeight(for: width)
            if filterHeights[id]?.constant != height { filterHeights[id]?.constant = height }
        }
    }

    private func presentError() {
        guard let error = model.error, shownError != error else { return }
        if showRetryableError(error.localizedDescription, retry: { [weak self] in
            self?.model.loadMore()
        }) { shownError = error }
    }

    private func makeLayout() -> UICollectionViewLayout {
        UICollectionViewCompositionalLayout { _, environment in
            let width = max(1, (environment.container.effectiveContentSize.width - 32 - 20) / 3)
            let item = NSCollectionLayoutItem(layoutSize: .init(widthDimension: .absolute(width),
                                                               heightDimension: .fractionalHeight(1)))
            let group = NSCollectionLayoutGroup.horizontal(layoutSize: .init(
                widthDimension: .fractionalWidth(1), heightDimension: .absolute(width * 1.5 + 39)),
                repeatingSubitem: item, count: 3)
            group.interItemSpacing = .fixed(FilmPosterMetrics.spacing)
            let section = NSCollectionLayoutSection(group: group)
            section.interGroupSpacing = 16
            section.contentInsets = .init(top: 4, leading: FilmPosterMetrics.inset, bottom: 16,
                                          trailing: FilmPosterMetrics.inset)
            return section
        }
    }

    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        collectionView.deselectItem(at: indexPath, animated: false)
        guard let id = dataSource.itemIdentifier(for: indexPath), let item = items[id] else { return }
        onSelectItem?(item)
    }

    public func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        (cell as? FilmPosterCell)?.resetPressFeedback()
    }

    public func scrollViewDidScroll(_ scrollView: UIScrollView) {
        guard scrollView === collection, scrollView.contentOffset.y > 0,
              scrollView.isDragging || scrollView.isDecelerating,
              model.error == nil, !model.isLoading,
              scrollView.contentOffset.y + scrollView.bounds.height > scrollView.contentSize.height - 200 else { return }
        model.loadMore()
    }

    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) { resetPressFeedback() }

    private func resetPressFeedback() {
        for cell in collection.visibleCells.compactMap({ $0 as? FilmPosterCell }) { cell.resetPressFeedback() }
    }
}

@MainActor
private final class FilmCategoryFilterRow: UIView {
    static let labelWidth: CGFloat = 52
    let label = UILabel()
    let tags: FilterTagsView

    init(title: String, id: String, themeProvider: any PageThemeProviding) {
        tags = FilterTagsView(themeProvider: themeProvider, identifier: "film.category.\(id)")
        super.init(frame: .zero)
        label.text = title
        label.font = .systemFont(ofSize: 13)
        addSubview(label)
        addSubview(tags)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func layoutSubviews() {
        super.layoutSubviews()
        label.frame = CGRect(x: 16, y: 4, width: Self.labelWidth - 16, height: 30)
        tags.frame = CGRect(x: Self.labelWidth, y: 0, width: max(0, bounds.width - Self.labelWidth), height: bounds.height)
    }
}
#endif
