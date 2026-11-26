#if os(iOS)
import UIKit
import MeowUI

/// Virtualized horizontal related/similar items rail backed by `MeowUI.IndexedDiffableProjection`.
@MainActor
final class FilmRelatedRailView: UIView, UICollectionViewDelegate, UICollectionViewDelegateFlowLayout, PageThemeable {
    var onSelect: ((FilmItem) -> Void)?
    private let images: ArtworkImages
    private let layout = UICollectionViewFlowLayout()
    private let collectionView: UICollectionView
    private let emptyLabel = UILabel()
    private var projection = IndexedDiffableProjection<Int, String, FilmItem>()
    private var dataSource: UICollectionViewDiffableDataSource<Int, String>!
    private var itemWidth: CGFloat = 88
    private var theme: PageTheme
    private var heightConstraint: NSLayoutConstraint!

    init(images: ArtworkImages, theme: PageTheme) {
        self.images = images
        self.theme = theme
        layout.scrollDirection = .horizontal
        layout.minimumLineSpacing = 12
        layout.sectionInset = UIEdgeInsets(top: 0, left: 16, bottom: 0, right: 16)
        collectionView = UICollectionView(frame: .zero, collectionViewLayout: layout)
        super.init(frame: .zero)

        collectionView.backgroundColor = .clear
        collectionView.showsHorizontalScrollIndicator = false
        collectionView.delaysContentTouches = false
        collectionView.delegate = self
        collectionView.register(PosterCatalogCell.self, forCellWithReuseIdentifier: PosterCatalogCell.reuseIdentifier)
        collectionView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(collectionView)
        collectionView.pinEdges(to: self)

        emptyLabel.text = "暂无相似作品"
        emptyLabel.font = .systemFont(ofSize: 14)
        emptyLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(emptyLabel)
        NSLayoutConstraint.activate([
            emptyLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16),
            emptyLabel.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])

        heightConstraint = heightAnchor.constraint(equalToConstant: 176)
        heightConstraint.isActive = true

        dataSource = UICollectionViewDiffableDataSource<Int, String>(collectionView: collectionView) { [weak self] cv, indexPath, id in
            let cell = cv.dequeueReusableCell(withReuseIdentifier: PosterCatalogCell.reuseIdentifier, for: indexPath) as! PosterCatalogCell
            if let self, let item = self.projection[id] {
                cell.configure(
                    card: PosterCatalogCard(
                        id: id,
                        title: item.title,
                        accessibilityIdentifier: "film.similar.\(item.sourceID).\(item.id)",
                        artwork: .url(item.posterURL, images: self.images)
                    ),
                    theme: self.theme
                )
            }
            return cell
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func apply(items: [FilmItem], viewportWidth: CGFloat, theme: PageTheme) {
        self.theme = theme
        itemWidth = PosterCatalogMetrics(viewportWidth: max(240, viewportWidth)).width
        emptyLabel.isHidden = !items.isEmpty
        emptyLabel.textColor = UIColor(theme.muted)
        collectionView.isHidden = items.isEmpty
        updateHeight(isEmpty: items.isEmpty)

        let update = projection.update(section: 0, items: items, id: { "\($0.sourceID):\($0.id)" })
        dataSource.applyIfNeeded(update, animatingDifferences: false)
    }

    func updateViewportWidth(_ width: CGFloat) {
        let next = PosterCatalogMetrics(viewportWidth: max(240, width)).width
        guard abs(itemWidth - next) > 0.5 else { return }
        itemWidth = next
        layout.invalidateLayout()
        updateHeight(isEmpty: projection.itemIDs.isEmpty)
    }

    private func updateHeight(isEmpty: Bool) {
        let target: CGFloat = isEmpty ? 36 : ceil(itemWidth * 1.5) + 46
        if abs(heightConstraint.constant - target) > 0.5 {
            heightConstraint.constant = target
        }
    }

    func applyTheme(_ theme: PageTheme) {
        self.theme = theme
        emptyLabel.textColor = UIColor(theme.muted)
        for cell in collectionView.visibleCells {
            (cell as? PosterCatalogCell)?.applyTheme(theme)
        }
    }

    func cancelVisibleArtworks() {
        for cell in collectionView.visibleCells {
            (cell as? PosterCatalogCell)?.cancelPendingLoad()
        }
    }

    func resumeVisibleArtworks() {
        for cell in collectionView.visibleCells {
            (cell as? PosterCatalogCell)?.resumePendingLoad()
        }
    }

    func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
                        sizeForItemAt indexPath: IndexPath) -> CGSize {
        CGSize(width: itemWidth, height: ceil(itemWidth * 1.5) + 42)
    }

    func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        guard let id = dataSource.itemIdentifier(for: indexPath), let item = projection[id] else { return }
        onSelect?(item)
    }

    func collectionView(_ collectionView: UICollectionView, willDisplay cell: UICollectionViewCell,
                        forItemAt indexPath: IndexPath) {
        (cell as? PosterCatalogCell)?.resumePendingLoad()
    }

    func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                        forItemAt indexPath: IndexPath) {
        guard let poster = cell as? PosterCatalogCell else { return }
        poster.resetPressFeedback()
        poster.cancelPendingLoad()
    }
}
#endif
