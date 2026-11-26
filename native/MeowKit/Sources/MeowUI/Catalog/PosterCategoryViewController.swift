#if os(iOS)
import UIKit

public struct PosterCategoryFilterRowModel: Equatable, Sendable {
    public let id: String
    public let title: String
    public let selectedID: String
    public let options: [FilterTag]

    public init(id: String, title: String, selectedID: String, options: [FilterTag]) {
        self.id = id
        self.title = title
        self.selectedID = selectedID
        self.options = options
    }
}

public struct PosterCategoryConfiguration: Sendable {
    public let pageAccessibilityID: String
    public let collectionAccessibilityID: String
    public let filterIdentifierPrefix: String
    public let moreButtonAccessibilityID: String
    public let loadingAccessibilityID: String
    public let emptyText: String

    public init(
        pageAccessibilityID: String,
        collectionAccessibilityID: String,
        filterIdentifierPrefix: String,
        moreButtonAccessibilityID: String,
        loadingAccessibilityID: String,
        emptyText: String
    ) {
        self.pageAccessibilityID = pageAccessibilityID
        self.collectionAccessibilityID = collectionAccessibilityID
        self.filterIdentifierPrefix = filterIdentifierPrefix
        self.moreButtonAccessibilityID = moreButtonAccessibilityID
        self.loadingAccessibilityID = loadingAccessibilityID
        self.emptyText = emptyText
    }
}

@MainActor
open class PosterCategoryViewController: PageViewController, UICollectionViewDelegate {
    private let configuration: PosterCategoryConfiguration
    private let filters = UIStackView()
    private lazy var emptyFiltersHeight = filters.heightAnchor.constraint(equalToConstant: 0)
    private var filterViews: [String: PosterCategoryFilterRow] = [:]
    private var filterHeights: [String: NSLayoutConstraint] = [:]
    private let empty = UILabel()

    private var renderedCards: [PosterCatalogCard] = []
    private var renderedFilterRows: [PosterCategoryFilterRowModel] = []
    private var isCategoryLoading = false
    private var categoryHasMore = true
    private var categoryHasError = false

    private var projection = IndexedDiffableProjection<Int, String, PosterCatalogCard>()
    private var dataSource: UICollectionViewDiffableDataSource<Int, String>!
    private lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: makeLayout())

    public init(configuration: PosterCategoryConfiguration, themeProvider: any PageThemeProviding) {
        self.configuration = configuration
        super.init(themeProvider: themeProvider)
    }

    open override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = configuration.pageAccessibilityID
        filters.axis = .vertical
        collection.backgroundColor = .clear
        collection.alwaysBounceVertical = true
        collection.delaysContentTouches = false
        collection.delegate = self
        collection.accessibilityIdentifier = configuration.collectionAccessibilityID
        collection.register(PosterCatalogCell.self, forCellWithReuseIdentifier: PosterCatalogCell.reuseIdentifier)
        collection.register(
            PosterCategoryFooterView.self,
            forSupplementaryViewOfKind: UICollectionView.elementKindSectionFooter,
            withReuseIdentifier: PosterCategoryFooterView.reuseIdentifier
        )
        dataSource = UICollectionViewDiffableDataSource(collectionView: collection) { [weak self] view, path, id in
            guard let self, let card = self.projection[id] else { return nil }
            let cell = view.dequeueReusableCell(withReuseIdentifier: PosterCatalogCell.reuseIdentifier, for: path)
            (cell as? PosterCatalogCell)?.configure(card: card, theme: self.theme, loadArtwork: self.isPageVisible)
            return cell
        }
        dataSource.supplementaryViewProvider = { [weak self] view, kind, path in
            guard let self, kind == UICollectionView.elementKindSectionFooter else { return nil }
            let footer = view.dequeueReusableSupplementaryView(
                ofKind: kind,
                withReuseIdentifier: PosterCategoryFooterView.reuseIdentifier,
                for: path
            )
            if let footer = footer as? PosterCategoryFooterView {
                self.configureFooter(footer)
            }
            return footer
        }
        collection.refreshControl = UIRefreshControl()
        collection.refreshControl?.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            guard self.isPageVisible else {
                self.collection.refreshControl?.endRefreshing()
                return
            }
            self.didRequestRefresh()
        }, for: .valueChanged)

        empty.font = .systemFont(ofSize: 15)
        empty.textAlignment = .center
        empty.text = configuration.emptyText
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
    }

    open override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        resumeVisibleArtworks()
    }

    open override func pageDidBecomeInactive() {
        cancelVisibleArtworks()
        super.pageDidBecomeInactive()
    }

    open override func pageDidBecomeHidden() {
        collection.refreshControl?.endRefreshing()
        resetPressFeedback()
        cancelVisibleArtworks()
        super.pageDidBecomeHidden()
    }

    open override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        updateFilterHeights()
    }

    open override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, dataSource != nil else { return }
        empty.textColor = UIColor(theme.muted)
        collection.refreshControl?.tintColor = UIColor(theme.accent)
        renderFilters(rows: renderedFilterRows)
        updateVisibleFooters()
        for cell in collection.visibleCells {
            (cell as? PosterCatalogCell)?.applyTheme(theme)
        }
    }

    public func applyCategoryState(
        filterRows: [PosterCategoryFilterRowModel],
        cards: [PosterCatalogCard],
        isLoading: Bool,
        hasMore: Bool,
        hasError: Bool
    ) {
        guard isViewLoaded, dataSource != nil else { return }
        renderedFilterRows = filterRows
        isCategoryLoading = isLoading
        categoryHasMore = hasMore
        categoryHasError = hasError
        renderFilters(rows: filterRows)
        if renderedCards != cards || dataSource.snapshot().sectionIdentifiers.isEmpty {
            let reset = !renderedCards.isEmpty && cards.isEmpty
            let update = projection.update(section: 0, items: cards, id: { $0.id })
            renderedCards = projection.items
            dataSource.applyIfNeeded(update, animatingDifferences: false)
            if reset { collection.setContentOffset(.zero, animated: false) }
        }
        empty.isHidden = !cards.isEmpty || isLoading || hasError
        if !isLoading { collection.refreshControl?.endRefreshing() }
        updateVisibleFooters()
    }

    open func didSelectCard(cardID: String, index: Int) {}
    open func didSelectFilter(rowID: String, value: String) {}
    open func didRequestRefresh() {}
    open func didRequestLoadMore() {}

    private func configureFooter(_ footer: PosterCategoryFooterView) {
        footer.configure(
            moreAccessibilityID: configuration.moreButtonAccessibilityID,
            loadingAccessibilityID: configuration.loadingAccessibilityID,
            isLoading: isCategoryLoading,
            hasItems: !renderedCards.isEmpty,
            hasMore: categoryHasMore,
            hasError: categoryHasError,
            theme: theme,
            onLoadMore: { [weak self] in self?.didRequestLoadMore() }
        )
    }

    private func updateVisibleFooters() {
        for path in collection.indexPathsForVisibleSupplementaryElements(ofKind: UICollectionView.elementKindSectionFooter) {
            if let footer = collection.supplementaryView(
                forElementKind: UICollectionView.elementKindSectionFooter,
                at: path
            ) as? PosterCategoryFooterView {
                configureFooter(footer)
            }
        }
    }

    private func renderFilters(rows: [PosterCategoryFilterRowModel]) {
        emptyFiltersHeight.isActive = rows.isEmpty
        for id in Array(filterViews.keys) where !rows.contains(where: { $0.id == id }) {
            filterViews.removeValue(forKey: id)?.removeFromSuperview()
            filterHeights.removeValue(forKey: id)
        }
        for (index, row) in rows.enumerated() {
            let rowView: PosterCategoryFilterRow
            if let existing = filterViews[row.id] {
                rowView = existing
            } else {
                rowView = PosterCategoryFilterRow(
                    title: row.title,
                    identifier: "\(configuration.filterIdentifierPrefix).\(row.id)",
                    themeProvider: themeProvider
                )
                filterViews[row.id] = rowView
                filters.insertArrangedSubview(rowView, at: index)
                let height = rowView.heightAnchor.constraint(equalToConstant: rowView.tags.collapsedHeight)
                height.isActive = true
                filterHeights[row.id] = height
                rowView.tags.onExpansionChanged = { [weak self] in self?.updateFilterHeights() }
            }
            rowView.label.textColor = UIColor(theme.muted)
            rowView.tags.configure(filters: row.options, selectedID: row.selectedID, theme: theme) { [weak self] value in
                guard let self else { return }
                self.didSelectFilter(rowID: row.id, value: value)
                self.collection.setContentOffset(.zero, animated: false)
            }
        }
        updateFilterHeights()
    }

    private func updateFilterHeights() {
        for (id, row) in filterViews {
            let width = max(0, filters.bounds.width - PosterCategoryFilterRow.labelWidth)
            row.tags.updateLayout(width: width)
            let height = row.tags.preferredHeight(for: width)
            if filterHeights[id]?.constant != height {
                filterHeights[id]?.constant = height
            }
        }
    }

    private func makeLayout() -> UICollectionViewLayout {
        UICollectionViewCompositionalLayout { _, environment in
            let width = max(1, (environment.container.effectiveContentSize.width - 32 - 20) / 3)
            let item = NSCollectionLayoutItem(layoutSize: .init(widthDimension: .absolute(width),
                                                               heightDimension: .fractionalHeight(1)))
            let group = NSCollectionLayoutGroup.horizontal(layoutSize: .init(
                widthDimension: .fractionalWidth(1), heightDimension: .absolute(width * 1.5 + 39)),
                repeatingSubitem: item, count: 3)
            group.interItemSpacing = .fixed(PosterCatalogMetrics.spacing)
            let section = NSCollectionLayoutSection(group: group)
            section.interGroupSpacing = 16
            section.contentInsets = .init(top: 4, leading: PosterCatalogMetrics.inset, bottom: 12,
                                          trailing: PosterCatalogMetrics.inset)
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
        guard let id = dataSource.itemIdentifier(for: indexPath) else { return }
        didSelectCard(cardID: id, index: indexPath.item)
    }

    public func collectionView(
        _ collectionView: UICollectionView,
        willDisplay cell: UICollectionViewCell,
        forItemAt indexPath: IndexPath
    ) {
        if isPageVisible {
            (cell as? PosterCatalogCell)?.resumePendingLoad()
        }
    }

    public func collectionView(
        _ collectionView: UICollectionView,
        didEndDisplaying cell: UICollectionViewCell,
        forItemAt indexPath: IndexPath
    ) {
        guard let poster = cell as? PosterCatalogCell else { return }
        poster.resetPressFeedback()
        poster.cancelPendingLoad()
    }

    public func scrollViewDidScroll(_ scrollView: UIScrollView) {
        guard scrollView === collection, scrollView.contentOffset.y > 0,
              scrollView.isDragging || scrollView.isDecelerating,
              !categoryHasError, !isCategoryLoading, categoryHasMore,
              scrollView.contentOffset.y + scrollView.bounds.height > scrollView.contentSize.height - 200 else { return }
        didRequestLoadMore()
    }

    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        resetPressFeedback()
    }

    private func resetPressFeedback() {
        for cell in collection.visibleCells.compactMap({ $0 as? PosterCatalogCell }) {
            cell.resetPressFeedback()
        }
    }

    private func cancelVisibleArtworks() {
        for cell in collection.visibleCells.compactMap({ $0 as? PosterCatalogCell }) {
            cell.cancelPendingLoad()
        }
    }

    private func resumeVisibleArtworks() {
        for cell in collection.visibleCells.compactMap({ $0 as? PosterCatalogCell }) {
            cell.resumePendingLoad()
        }
    }
}

@MainActor
private final class PosterCategoryFooterView: UICollectionReusableView {
    static let reuseIdentifier = "PosterCategoryFooterView"
    private let more = UIButton(type: .system)
    private let activity = UIActivityIndicatorView(style: .medium)
    private var onLoadMore: (() -> Void)?

    override init(frame: CGRect) {
        super.init(frame: frame)
        more.titleLabel?.font = .systemFont(ofSize: 13)
        more.addAction(UIAction { [weak self] _ in self?.onLoadMore?() }, for: .touchUpInside)
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

    func configure(
        moreAccessibilityID: String,
        loadingAccessibilityID: String,
        isLoading: Bool,
        hasItems: Bool,
        hasMore: Bool,
        hasError: Bool,
        theme: PageTheme,
        onLoadMore: @escaping () -> Void
    ) {
        self.onLoadMore = onLoadMore
        more.accessibilityIdentifier = moreAccessibilityID
        activity.accessibilityIdentifier = loadingAccessibilityID
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
private final class PosterCategoryFilterRow: UIView {
    static let labelWidth: CGFloat = 52
    let label = UILabel()
    let tags: FilterTagsView

    init(title: String, identifier: String, themeProvider: any PageThemeProviding) {
        tags = FilterTagsView(themeProvider: themeProvider, identifier: identifier)
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
