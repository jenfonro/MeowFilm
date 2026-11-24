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
        collectionView.register(FilmRelatedCell.self, forCellWithReuseIdentifier: FilmRelatedCell.reuseIdentifier)
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
            let cell = cv.dequeueReusableCell(withReuseIdentifier: FilmRelatedCell.reuseIdentifier, for: indexPath) as! FilmRelatedCell
            if let self, let item = self.projection[id] {
                cell.configure(item: item, images: self.images, theme: self.theme)
            }
            return cell
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func apply(items: [FilmItem], viewportWidth: CGFloat, theme: PageTheme) {
        self.theme = theme
        itemWidth = FilmPosterMetrics(viewportWidth: max(240, viewportWidth)).width
        emptyLabel.isHidden = !items.isEmpty
        emptyLabel.textColor = UIColor(theme.muted)
        collectionView.isHidden = items.isEmpty
        updateHeight(isEmpty: items.isEmpty)

        let update = projection.update(section: 0, items: items, id: { "\($0.sourceID):\($0.id)" })
        dataSource.applyIfNeeded(update, animatingDifferences: false)
    }

    func updateViewportWidth(_ width: CGFloat) {
        let next = FilmPosterMetrics(viewportWidth: max(240, width)).width
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
            (cell as? FilmRelatedCell)?.applyTheme(theme)
        }
    }

    func cancelVisibleArtworks() {
        for cell in collectionView.visibleCells {
            (cell as? FilmRelatedCell)?.cancelPendingLoad()
        }
    }

    func resumeVisibleArtworks() {
        for cell in collectionView.visibleCells {
            (cell as? FilmRelatedCell)?.resumePendingLoad()
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
        (cell as? FilmRelatedCell)?.resumePendingLoad()
    }

    func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                        forItemAt indexPath: IndexPath) {
        (cell as? FilmRelatedCell)?.cancelPendingLoad()
    }
}

@MainActor
private final class FilmRelatedCell: UICollectionViewCell {
    static let reuseIdentifier = "film.detail.related.cell"

    private var artwork: ArtworkView?
    private let heading = UILabel()
    private let stack = UIStackView()
    private var currentURL: URL?
    private lazy var feedback = PressFeedback(view: contentView)

    override var isHighlighted: Bool {
        didSet { feedback.setPressed(isHighlighted) }
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        heading.font = .systemFont(ofSize: 13)
        heading.numberOfLines = 2
        stack.axis = .vertical
        stack.spacing = 7
        stack.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(stack)
        stack.pinEdges(to: contentView)
        isAccessibilityElement = true
        accessibilityTraits = .button
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        feedback.setPressed(false)
        currentURL = nil
        artwork?.load(nil)
    }

    func cancelPendingLoad() {
        artwork?.cancelPendingLoad()
    }

    func resumePendingLoad() {
        guard let currentURL else { return }
        artwork?.load(currentURL)
    }

    func configure(item: FilmItem, images: ArtworkImages, theme: PageTheme) {
        let art: ArtworkView
        if let existing = artwork {
            art = existing
        } else {
            let created = ArtworkView(size: 0, images: images)
            created.layer.cornerRadius = 7
            created.heightAnchor.constraint(equalTo: created.widthAnchor, multiplier: 1.5).isActive = true
            stack.addArrangedSubview(created)
            stack.addArrangedSubview(heading)
            artwork = created
            art = created
        }
        if currentURL != item.posterURL {
            currentURL = item.posterURL
            art.load(item.posterURL)
        }
        heading.text = item.title
        accessibilityLabel = item.title
        accessibilityIdentifier = "film.similar.\(item.sourceID).\(item.id)"
        applyTheme(theme)
    }

    func applyTheme(_ theme: PageTheme) {
        artwork?.backgroundColor = UIColor(theme.chip)
        heading.textColor = UIColor(theme.ink)
    }
}
#endif
