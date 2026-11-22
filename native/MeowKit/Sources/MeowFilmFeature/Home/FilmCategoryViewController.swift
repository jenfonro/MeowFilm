#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class FilmCategoryViewController: PageViewController, UICollectionViewDelegate {
    public var onSelectItem: ((FilmItem) -> Void)?
    let model: FilmCategoryModel
    private let images: ArtworkImages
    private let filters = UIStackView()
    private lazy var emptyFiltersHeight = filters.heightAnchor.constraint(equalToConstant: 0)
    private var filterViews: [String: FilmCategoryFilterRow] = [:]
    private var filterHeights: [String: NSLayoutConstraint] = [:]
    private let empty = UILabel()
    private var shownError: FilmHomeError?
    var renderedItems: [FilmItem] = []
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
        collection.register(
            FilmCategoryFooterView.self,
            forSupplementaryViewOfKind: UICollectionView.elementKindSectionFooter,
            withReuseIdentifier: FilmCategoryFooterView.reuseIdentifier
        )
        dataSource = UICollectionViewDiffableDataSource(collectionView: collection) { [weak self] view, path, id in
            guard let self, let item = self.items[id] else { return nil }
            let cell = view.dequeueReusableCell(withReuseIdentifier: FilmPosterCell.reuseIdentifier, for: path)
            (cell as? FilmPosterCell)?.configure(item: item, images: self.images, theme: self.theme)
            return cell
        }
        dataSource.supplementaryViewProvider = { [weak self] view, kind, path in
            guard let self, kind == UICollectionView.elementKindSectionFooter else { return nil }
            let footer = view.dequeueReusableSupplementaryView(
                ofKind: kind,
                withReuseIdentifier: FilmCategoryFooterView.reuseIdentifier,
                for: path
            )
            if let footer = footer as? FilmCategoryFooterView {
                self.configureFooter(footer)
            }
            return footer
        }
        collection.refreshControl = UIRefreshControl()
        collection.refreshControl?.addAction(UIAction { [weak self] _ in self?.model.refresh() }, for: .valueChanged)
        empty.font = .systemFont(ofSize: 15)
        empty.textAlignment = .center
        empty.text = "暂无影片"
        collection.backgroundView = empty
        for child in [filters, collection] {
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
            collection.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        observe(model, whenVisible: true) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeVisible() {
        model.loadIfNeeded()
        render()
        resumeVisibleArtworks()
    }
    public override func pageDidBecomeInactive() {
        cancelVisibleArtworks()
        model.close()
    }
    public override func pageDidBecomeHidden() {
        model.cancel()
        resetPressFeedback()
        cancelVisibleArtworks()
    }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        updateFilterHeights()
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, dataSource != nil else { return }
        empty.textColor = UIColor(theme.muted)
        renderFilters()
        updateVisibleFooters()
        for path in collection.indexPathsForVisibleItems {
            guard let id = dataSource.itemIdentifier(for: path),
                  let item = items[id],
                  let cell = collection.cellForItem(at: path) as? FilmPosterCell else { continue }
            cell.configure(item: item, images: images, theme: theme)
        }
    }

    private func render() {
        guard isViewLoaded, dataSource != nil else { return }
        renderFilters()
        if renderedItems != model.items || dataSource.snapshot().sectionIdentifiers.isEmpty {
            let reset = !renderedItems.isEmpty && model.items.isEmpty
            renderedItems = model.items
            var seen: Set<FilmItemIdentity> = []
            var uniqueIDs: [FilmItemIdentity] = []
            uniqueIDs.reserveCapacity(model.items.count)
            var nextItems: [FilmItemIdentity: FilmItem] = [:]
            for item in model.items where seen.insert(item.identity).inserted {
                uniqueIDs.append(item.identity)
                nextItems[item.identity] = item
            }
            items = nextItems
            var snapshot = NSDiffableDataSourceSnapshot<Int, FilmItemIdentity>()
            snapshot.appendSections([0])
            snapshot.appendItems(uniqueIDs)
            let existingIDs = Set(dataSource.snapshot().itemIdentifiers)
            snapshot.reconfigureItems(uniqueIDs.filter { existingIDs.contains($0) })
            dataSource.apply(snapshot, animatingDifferences: false)
            if reset { collection.setContentOffset(.zero, animated: false) }
        }
        empty.isHidden = !model.items.isEmpty || model.isLoading || model.error != nil
        if !model.isLoading { collection.refreshControl?.endRefreshing() }
        updateVisibleFooters()
        if model.error == nil { shownError = nil }
        presentError()
    }

    private func configureFooter(_ footer: FilmCategoryFooterView) {
        footer.configure(
            isLoading: model.isLoading,
            hasItems: !model.items.isEmpty,
            hasMore: model.hasMore,
            hasError: model.error != nil,
            theme: theme,
            onLoadMore: { [weak self] in self?.model.loadMore() }
        )
    }

    private func updateVisibleFooters() {
        for path in collection.indexPathsForVisibleSupplementaryElements(ofKind: UICollectionView.elementKindSectionFooter) {
            if let footer = collection.supplementaryView(
                forElementKind: UICollectionView.elementKindSectionFooter, at: path
            ) as? FilmCategoryFooterView {
                configureFooter(footer)
            }
        }
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
            row.updateFadeMask()
        }
    }

    private func presentError() {
        guard let error = model.error, shownError != error else { return }
        shownError = error
        enqueueError(error.localizedDescription, retry: { [weak self] in
            self?.shownError = nil
            self?.model.loadMore()
        })
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
            section.contentInsets = .init(top: 4, leading: FilmPosterMetrics.inset, bottom: 12,
                                          trailing: FilmPosterMetrics.inset)
            let footer = NSCollectionLayoutBoundarySupplementaryItem(
                layoutSize: .init(widthDimension: .fractionalWidth(1), heightDimension: .absolute(52)),
                elementKind: UICollectionView.elementKindSectionFooter,
                alignment: .bottom
            )
            section.boundarySupplementaryItems = [footer]
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
        guard let poster = cell as? FilmPosterCell else { return }
        poster.resetPressFeedback()
        poster.cancelPendingLoad()
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

    private func cancelVisibleArtworks() {
        for cell in collection.visibleCells.compactMap({ $0 as? FilmPosterCell }) { cell.cancelPendingLoad() }
    }

    private func resumeVisibleArtworks() {
        for cell in collection.visibleCells.compactMap({ $0 as? FilmPosterCell }) { cell.resumePendingLoad() }
    }
}

@MainActor
private final class FilmCategoryFooterView: UICollectionReusableView {
    static let reuseIdentifier = "FilmCategoryFooterView"
    private let more = UIButton(type: .system)
    private let activity = UIActivityIndicatorView(style: .medium)
    private var onLoadMore: (() -> Void)?

    override init(frame: CGRect) {
        super.init(frame: frame)
        more.accessibilityIdentifier = "film.category.more"
        more.titleLabel?.font = .systemFont(ofSize: 13)
        more.addAction(UIAction { [weak self] _ in self?.onLoadMore?() }, for: .touchUpInside)
        activity.accessibilityIdentifier = "film.category.loading"
        activity.hidesWhenStopped = true
        for child in [more, activity] {
            addSubview(child)
            child.translatesAutoresizingMaskIntoConstraints = false
        }
        NSLayoutConstraint.activate([
            more.topAnchor.constraint(equalTo: topAnchor),
            more.bottomAnchor.constraint(equalTo: bottomAnchor),
            more.leadingAnchor.constraint(equalTo: leadingAnchor),
            more.trailingAnchor.constraint(equalTo: trailingAnchor),
            activity.centerXAnchor.constraint(equalTo: centerXAnchor),
            activity.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        onLoadMore = nil
        activity.stopAnimating()
    }

    func configure(isLoading: Bool, hasItems: Bool, hasMore: Bool, hasError: Bool,
                   theme: PageTheme, onLoadMore: @escaping () -> Void) {
        self.onLoadMore = onLoadMore
        activity.color = UIColor(theme.muted)
        more.tintColor = UIColor(theme.accent)
        if isLoading {
            more.setTitle(nil, for: .normal)
            more.isEnabled = false
            more.isHidden = false
            activity.startAnimating()
        } else {
            activity.stopAnimating()
            more.isHidden = !hasItems
            more.setTitle(hasMore ? "加载更多" : "没有更多了", for: .normal)
            more.isEnabled = hasItems && !hasError && hasMore
        }
    }
}

@MainActor
private final class FilmCategoryFilterRow: UIView {
    static let labelWidth: CGFloat = 52
    private static let trailingFadeWidth: CGFloat = 24
    private static let collapsedScrollTrailingInset: CGFloat = 55
    let label = UILabel()
    let tags: FilterTagsView
    private let fadeMask = CAGradientLayer()

    init(title: String, id: String, themeProvider: any PageThemeProviding) {
        tags = FilterTagsView(themeProvider: themeProvider, identifier: "film.category.\(id)")
        super.init(frame: .zero)
        label.text = title
        label.font = .systemFont(ofSize: 13)
        fadeMask.startPoint = CGPoint(x: 0, y: 0.5)
        fadeMask.endPoint = CGPoint(x: 1, y: 0.5)
        fadeMask.colors = [
            UIColor.black.cgColor,
            UIColor.black.cgColor,
            UIColor.clear.cgColor,
            UIColor.black.cgColor,
            UIColor.black.cgColor,
        ]
        addSubview(label)
        addSubview(tags)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func layoutSubviews() {
        super.layoutSubviews()
        label.frame = CGRect(x: 16, y: 4, width: Self.labelWidth - 16, height: 30)
        tags.frame = CGRect(x: Self.labelWidth, y: 0, width: max(0, bounds.width - Self.labelWidth), height: bounds.height)
        updateFadeMask()
    }

    func updateFadeMask() {
        let width = tags.bounds.width
        guard tags.hasOverflow, !tags.isExpanded, width > 96 else {
            if tags.layer.mask != nil { tags.layer.mask = nil }
            return
        }
        let scrollMaxX = max(16, width - Self.collapsedScrollTrailingInset)
        let fadeStartX = max(16, scrollMaxX - Self.trailingFadeWidth)
        let restoreX = min(width, scrollMaxX + 4)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        fadeMask.frame = tags.bounds
        fadeMask.locations = [
            0,
            NSNumber(value: Double(fadeStartX / width)),
            NSNumber(value: Double(scrollMaxX / width)),
            NSNumber(value: Double(restoreX / width)),
            1,
        ]
        if tags.layer.mask !== fadeMask {
            tags.layer.mask = fadeMask
        }
        CATransaction.commit()
    }
}
#endif
