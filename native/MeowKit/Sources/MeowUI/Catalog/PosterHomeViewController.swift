#if os(iOS)
import UIKit

public struct PosterHomeConfiguration: Sendable {
    public let pageAccessibilityID: String
    public let collectionAccessibilityID: String
    public let moreIdentifierPrefix: String

    public init(
        pageAccessibilityID: String,
        collectionAccessibilityID: String,
        moreIdentifierPrefix: String
    ) {
        self.pageAccessibilityID = pageAccessibilityID
        self.collectionAccessibilityID = collectionAccessibilityID
        self.moreIdentifierPrefix = moreIdentifierPrefix
    }
}

@MainActor
open class PosterHomeViewController: PageViewController, UICollectionViewDelegate {
    private struct Row: Hashable, Sendable {
        let section: String
        let item: String?
    }
    private struct RowEntry: Equatable {
        let row: Row
        let card: PosterCatalogCard?
        let emptyStatusText: String
    }

    private let configuration: PosterHomeConfiguration
    private let activity = UIActivityIndicatorView(style: .medium)
    private let status = UILabel()
    private let stateView = UIStackView()
    private var renderedSections: [PosterCatalogSection] = []
    private var renderedSourceID: String?
    private var projection = IndexedDiffableProjection<String, Row, RowEntry>()
    private var dataSource: UICollectionViewDiffableDataSource<String, Row>!
    private lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: makeLayout())

    public init(configuration: PosterHomeConfiguration, themeProvider: any PageThemeProviding) {
        self.configuration = configuration
        super.init(themeProvider: themeProvider)
    }

    open override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = configuration.pageAccessibilityID
        collection.backgroundColor = .clear
        collection.accessibilityIdentifier = configuration.collectionAccessibilityID
        collection.delegate = self
        collection.alwaysBounceVertical = true
        collection.delaysContentTouches = false
        collection.register(PosterCatalogCell.self, forCellWithReuseIdentifier: PosterCatalogCell.reuseIdentifier)
        collection.register(PosterEmptyCell.self, forCellWithReuseIdentifier: PosterEmptyCell.reuseIdentifier)
        collection.register(
            PosterSectionHeaderView.self,
            forSupplementaryViewOfKind: UICollectionView.elementKindSectionHeader,
            withReuseIdentifier: PosterSectionHeaderView.reuseIdentifier
        )
        dataSource = UICollectionViewDiffableDataSource(collectionView: collection) { [weak self] view, path, row in
            guard let self, let entry = self.projection[row] else { return nil }
            guard let card = entry.card else {
                let cell = view.dequeueReusableCell(withReuseIdentifier: PosterEmptyCell.reuseIdentifier, for: path)
                if let cell = cell as? PosterEmptyCell {
                    cell.label.text = entry.emptyStatusText
                    cell.label.textColor = UIColor(self.theme.muted)
                }
                return cell
            }
            let cell = view.dequeueReusableCell(withReuseIdentifier: PosterCatalogCell.reuseIdentifier, for: path)
            (cell as? PosterCatalogCell)?.configure(card: card, theme: self.theme, loadArtwork: self.isPageVisible)
            return cell
        }
        dataSource.supplementaryViewProvider = { [weak self] view, kind, path in
            guard let self, let section = self.section(at: path.section) else { return nil }
            let header = view.dequeueReusableSupplementaryView(
                ofKind: kind,
                withReuseIdentifier: PosterSectionHeaderView.reuseIdentifier,
                for: path
            )
            self.configureHeader(header, section: section)
            return header
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

    open override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        status.textColor = UIColor(theme.muted)
        activity.color = UIColor(theme.muted)
        collection.refreshControl?.tintColor = UIColor(theme.accent)
        guard dataSource != nil else { return }
        for cell in collection.visibleCells {
            if let poster = cell as? PosterCatalogCell {
                poster.applyTheme(theme)
            } else if let emptyCell = cell as? PosterEmptyCell {
                emptyCell.label.textColor = UIColor(theme.muted)
            }
        }
        updateVisibleHeaders()
    }

    public func applyHomeState(
        sourceID: String?,
        sections: [PosterCatalogSection],
        isLoading: Bool,
        hasError: Bool,
        emptyText: String
    ) {
        guard isViewLoaded, dataSource != nil else { return }
        if renderedSections != sections || renderedSourceID != sourceID {
            let switchedSource = renderedSourceID != sourceID
            renderedSourceID = sourceID
            renderedSections = sections
            let projectedSections = sections.map { section -> (id: String, items: [RowEntry]) in
                let entries = section.items.map {
                    RowEntry(row: Row(section: section.id, item: $0.id), card: $0, emptyStatusText: "")
                }
                let normalized = entries.isEmpty
                    ? [RowEntry(row: Row(section: section.id, item: nil), card: nil, emptyStatusText: section.statusText)]
                    : entries
                return (id: section.id, items: normalized)
            }
            let update = projection.update(sections: projectedSections, id: { $0.row })
            dataSource.applyIfNeeded(update, animatingDifferences: false) { [weak self] in
                self?.updateVisibleHeaders()
            }
            collection.collectionViewLayout.invalidateLayout()
            if switchedSource {
                collection.setContentOffset(.zero, animated: false)
            }
        }
        stateView.isHidden = !sections.isEmpty
        let showEmptyLabel = !isLoading && !hasError
        status.text = showEmptyLabel ? emptyText : nil
        status.isHidden = !showEmptyLabel
        if isLoading && sections.isEmpty {
            activity.startAnimating()
        } else {
            activity.stopAnimating()
        }
        if !isLoading {
            collection.refreshControl?.endRefreshing()
        }
    }

    open func didRequestRefresh() {}
    open func didTapSectionMore(sectionID: String) {}
    open func didTapSectionAction(sectionID: String) {}
    open func didSelectCard(inSection sectionID: String, cardID: String, index: Int) {}
    open func didDisplaySection(sectionID: String) {}

    private func updateVisibleHeaders() {
        let identifiers = dataSource.snapshot().sectionIdentifiers
        for path in collection.indexPathsForVisibleSupplementaryElements(ofKind: UICollectionView.elementKindSectionHeader) {
            guard identifiers.indices.contains(path.section),
                  let section = renderedSections.first(where: { $0.id == identifiers[path.section] }),
                  let header = collection.supplementaryView(
                      forElementKind: UICollectionView.elementKindSectionHeader,
                      at: path
                  ) else { continue }
            configureHeader(header, section: section)
        }
    }

    private func configureHeader(_ header: UICollectionReusableView, section: PosterCatalogSection) {
        let sectionID = section.id
        let action: (() -> Void)? = section.hasMore ? { [weak self] in
            self?.didTapSectionMore(sectionID: sectionID)
        } : nil
        let trailingAction: (() -> Void)? = section.actionTitle != nil ? { [weak self] in
            self?.didTapSectionAction(sectionID: sectionID)
        } : nil
        (header as? PosterSectionHeaderView)?.configure(
            title: section.title,
            theme: theme,
            moreAccessibilityID: "\(configuration.moreIdentifierPrefix).\(section.id)",
            onMore: action,
            actionTitle: section.actionTitle,
            onAction: trailingAction
        )
    }

    private func section(at index: Int) -> PosterCatalogSection? {
        guard let dataSource else { return nil }
        let identifiers = dataSource.snapshot().sectionIdentifiers
        guard identifiers.indices.contains(index) else { return nil }
        return renderedSections.first { $0.id == identifiers[index] }
    }

    private func makeLayout() -> UICollectionViewLayout {
        UICollectionViewCompositionalLayout { [weak self] index, environment in
            let empty = self?.section(at: index)?.items.isEmpty ?? true
            let metrics = PosterCatalogMetrics(viewportWidth: environment.container.effectiveContentSize.width)
            let item = NSCollectionLayoutItem(layoutSize: .init(widthDimension: .fractionalWidth(1),
                                                               heightDimension: .fractionalHeight(1)))
            let group = NSCollectionLayoutGroup.horizontal(layoutSize: .init(
                widthDimension: empty ? .fractionalWidth(1) : .absolute(metrics.width),
                heightDimension: .absolute(empty ? 42 : metrics.height)), subitems: [item])
            let section = NSCollectionLayoutSection(group: group)
            section.orthogonalScrollingBehavior = empty ? .none : .continuous
            section.interGroupSpacing = PosterCatalogMetrics.spacing
            section.contentInsets = .init(top: 2, leading: PosterCatalogMetrics.inset, bottom: 20,
                                          trailing: PosterCatalogMetrics.inset)
            let heading = NSCollectionLayoutBoundarySupplementaryItem(layoutSize: .init(
                widthDimension: .fractionalWidth(1), heightDimension: .absolute(44)),
                elementKind: UICollectionView.elementKindSectionHeader, alignment: .top)
            section.boundarySupplementaryItems = [heading]
            return section
        }
    }

    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        collectionView.deselectItem(at: indexPath, animated: false)
        guard let row = dataSource.itemIdentifier(for: indexPath),
              let card = projection[row]?.card else { return }
        didSelectCard(inSection: row.section, cardID: card.id, index: indexPath.item)
    }

    public func collectionView(
        _ collectionView: UICollectionView,
        willDisplay cell: UICollectionViewCell,
        forItemAt indexPath: IndexPath
    ) {
        if let section = section(at: indexPath.section) {
            didDisplaySection(sectionID: section.id)
        }
        if isPageVisible {
            (cell as? PosterCatalogCell)?.resumePendingLoad()
        }
    }

    public func collectionView(
        _ collectionView: UICollectionView,
        willDisplaySupplementaryView view: UICollectionReusableView,
        forElementKind elementKind: String,
        at indexPath: IndexPath
    ) {
        if elementKind == UICollectionView.elementKindSectionHeader,
           let section = section(at: indexPath.section) {
            didDisplaySection(sectionID: section.id)
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
#endif
