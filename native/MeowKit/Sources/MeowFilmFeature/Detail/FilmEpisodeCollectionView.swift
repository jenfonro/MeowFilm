#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmTouchScrollView: UIScrollView {
    override func touchesShouldCancel(in view: UIView) -> Bool { true }
}

struct FilmHeroGeometry {
    /// The poster starts slightly enlarged, so shrinking it never uncovers its fixed frame.
    static let restingZoom: CGFloat = 1.12
    let progress: CGFloat
    let zoom: CGFloat
    let translation: CGFloat

    init(offset: CGFloat, height: CGFloat, headerHeight: CGFloat, reduceMotion: Bool) {
        progress = min(1, max(0, offset / max(1, height - headerHeight)))
        zoom = reduceMotion ? 1 : Self.restingZoom - (Self.restingZoom - 1) * progress
        translation = -max(0, offset)
    }

    var headerAlpha: CGFloat { min(1, max(0, 5 * progress - 4)) }
}

/// Darkens only the status bar and header band over poster art and fades out as the header appears.
@MainActor
final class FilmHeroScrimView: UIView {
    override class var layerClass: AnyClass { CAGradientLayer.self }

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        alpha = 0
        (layer as? CAGradientLayer)?.colors = [UIColor.black.withAlphaComponent(0.45).cgColor,
                                               UIColor.black.withAlphaComponent(0).cgColor]
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}

@MainActor
struct FilmEpisodeListItem {
    let id: String
    let episode: FilmEpisode
    let accessibilityID: String
    var enabled: Bool
    let onSelect: (@MainActor () -> Void)?
}

/// Virtualized episode list supporting both horizontal card rail and bounded vertical list
/// backed by `MeowUI.IndexedDiffableProjection` and reusable `FilmEpisodeCell`.
@MainActor
final class FilmEpisodeCollectionView: UIView, UICollectionViewDelegate, UICollectionViewDelegateFlowLayout {
    private let images: ArtworkImages
    private let layout = UICollectionViewFlowLayout()
    let collectionView: UICollectionView
    private var projection = IndexedDiffableProjection<Int, String, FilmEpisodeListItem>()
    private var dataSource: UICollectionViewDiffableDataSource<Int, String>!
    private var heightConstraint: NSLayoutConstraint!
    private var isListMode = false
    private var viewportWidth: CGFloat = 375
    private var theme: PageTheme

    var currentItems: [FilmEpisodeListItem] { projection.items }
    var contentOffset: CGPoint { collectionView.contentOffset }
    func setContentOffset(_ offset: CGPoint, animated: Bool = false) {
        collectionView.setContentOffset(offset, animated: animated)
    }

    init(images: ArtworkImages, theme: PageTheme) {
        self.images = images
        self.theme = theme
        layout.scrollDirection = .horizontal
        layout.minimumLineSpacing = 12
        layout.minimumInteritemSpacing = 12
        layout.sectionInset = UIEdgeInsets(top: 0, left: 16, bottom: 0, right: 16)
        collectionView = UICollectionView(frame: .zero, collectionViewLayout: layout)
        super.init(frame: .zero)

        collectionView.backgroundColor = .clear
        collectionView.showsHorizontalScrollIndicator = false
        collectionView.showsVerticalScrollIndicator = true
        collectionView.delaysContentTouches = false
        collectionView.delegate = self
        collectionView.register(FilmEpisodeCell.self, forCellWithReuseIdentifier: FilmEpisodeCell.reuseIdentifier)
        collectionView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(collectionView)
        collectionView.pinEdges(to: self)
        heightConstraint = heightAnchor.constraint(equalToConstant: 190)
        heightConstraint.isActive = true

        dataSource = UICollectionViewDiffableDataSource<Int, String>(collectionView: collectionView) { [weak self] cv, indexPath, id in
            let cell = cv.dequeueReusableCell(withReuseIdentifier: FilmEpisodeCell.reuseIdentifier, for: indexPath) as! FilmEpisodeCell
            if let self, let item = self.projection[id] {
                cell.configure(item: item, images: self.images, list: self.isListMode, theme: self.theme)
            }
            return cell
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func apply(items: [FilmEpisodeListItem], isList: Bool, viewportWidth: CGFloat, theme: PageTheme) {
        self.theme = theme
        let layoutChanged = isListMode != isList || abs(self.viewportWidth - viewportWidth) > 0.5
        isListMode = isList
        self.viewportWidth = max(240, viewportWidth)
        collectionView.accessibilityIdentifier = isList ? "film.detail.episode-list" : "film.detail.episode-cards"

        if layoutChanged {
            layout.scrollDirection = isList ? .vertical : .horizontal
            layout.minimumLineSpacing = isList ? 14 : 12
            layout.sectionInset = UIEdgeInsets(top: 0, left: 16, bottom: 0, right: 16)
            layout.invalidateLayout()
        }

        let update = projection.update(
            section: 0,
            items: items,
            id: { $0.id },
            isChanged: { prev, next in
                layoutChanged || prev.episode != next.episode || prev.enabled != next.enabled ||
                    prev.accessibilityID != next.accessibilityID
            }
        )
        dataSource.applyIfNeeded(update, animatingDifferences: false)
        updateHeight(itemCount: projection.itemIDs.count)
    }

    func updateViewportWidth(_ width: CGFloat) {
        let next = max(240, width)
        guard abs(viewportWidth - next) > 0.5 else { return }
        viewportWidth = next
        layout.invalidateLayout()
        updateHeight(itemCount: projection.itemIDs.count)
    }

    func updateAvailability(_ available: Bool) {
        let updatedItems = projection.items.map { item -> FilmEpisodeListItem in
            var copy = item
            copy.enabled = available && item.onSelect != nil
            return copy
        }
        let update = projection.update(
            section: 0,
            items: updatedItems,
            id: { $0.id },
            isChanged: { $0.enabled != $1.enabled }
        )
        guard !update.changedItemIDs.isEmpty else { return }
        for indexPath in collectionView.indexPathsForVisibleItems {
            guard let id = dataSource.itemIdentifier(for: indexPath),
                  let item = projection[id],
                  let cell = collectionView.cellForItem(at: indexPath) as? FilmEpisodeCell else { continue }
            cell.setItemEnabled(item.enabled)
        }
    }

    func applyTheme(_ theme: PageTheme) {
        self.theme = theme
        for cell in collectionView.visibleCells {
            (cell as? FilmEpisodeCell)?.applyTheme(theme)
        }
    }

    func cancelVisibleArtworks() {
        for cell in collectionView.visibleCells {
            (cell as? FilmEpisodeCell)?.cancelPendingLoad()
        }
    }

    func resumeVisibleArtworks() {
        for cell in collectionView.visibleCells {
            (cell as? FilmEpisodeCell)?.resumePendingLoad()
        }
    }

    private func cardSize() -> CGSize {
        let width = min(250, floor(viewportWidth * 0.44))
        let artHeight = ceil(width * 9.0 / 16.0)
        return CGSize(width: width, height: artHeight + 82)
    }

    private func listItemSize() -> CGSize {
        let width = max(200, viewportWidth - 32)
        let artWidth = floor(width * 0.36)
        let artHeight = ceil(artWidth * 9.0 / 16.0)
        return CGSize(width: width, height: max(84, artHeight + 12))
    }

    private func updateHeight(itemCount: Int) {
        let target: CGFloat
        if isListMode {
            let row = listItemSize().height
            let total = itemCount > 0 ? CGFloat(itemCount) * row + CGFloat(max(0, itemCount - 1)) * 14 : 0
            target = min(max(row, total), 460)
            collectionView.isScrollEnabled = total > 460
        } else {
            target = cardSize().height
            collectionView.isScrollEnabled = true
        }
        if abs(heightConstraint.constant - target) > 0.5 {
            heightConstraint.constant = target
        }
    }

    func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
                        sizeForItemAt indexPath: IndexPath) -> CGSize {
        isListMode ? listItemSize() : cardSize()
    }

    func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        guard let id = dataSource.itemIdentifier(for: indexPath),
              let item = projection[id], item.enabled else { return }
        item.onSelect?()
    }

    func collectionView(_ collectionView: UICollectionView, willDisplay cell: UICollectionViewCell,
                        forItemAt indexPath: IndexPath) {
        (cell as? FilmEpisodeCell)?.resumePendingLoad()
    }

    func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                        forItemAt indexPath: IndexPath) {
        (cell as? FilmEpisodeCell)?.cancelPendingLoad()
    }
}

@MainActor
private final class FilmEpisodeCell: UICollectionViewCell {
    static let reuseIdentifier = "film.detail.episode.cell"

    private var artwork: ArtworkView?
    private let heading = UILabel()
    private let summary = UILabel()
    private let textStack = UIStackView()
    private let stack = UIStackView()
    private var listWidthConstraint: NSLayoutConstraint?
    private var artAspectConstraint: NSLayoutConstraint?
    private var isList = false
    private var currentURL: URL?
    private lazy var feedback = PressFeedback(view: contentView)

    override var isHighlighted: Bool {
        didSet { feedback.setPressed(isHighlighted) }
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        heading.font = .systemFont(ofSize: 14)
        heading.numberOfLines = 2
        summary.font = .systemFont(ofSize: 12)
        summary.numberOfLines = 3
        textStack.axis = .vertical
        textStack.spacing = 4
        textStack.addArrangedSubview(heading)
        textStack.addArrangedSubview(summary)
        stack.axis = .vertical
        stack.alignment = .fill
        stack.spacing = 6
        stack.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(stack)
        stack.pinEdges(to: contentView)
        isAccessibilityElement = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        feedback.setPressed(false)
        currentURL = nil
        artwork?.load(nil)
        accessibilityIdentifier = nil
        accessibilityLabel = nil
    }

    func cancelPendingLoad() {
        artwork?.cancelPendingLoad()
    }

    func resumePendingLoad() {
        guard let currentURL else { return }
        artwork?.load(currentURL)
    }

    private func ensureArtwork(images: ArtworkImages) -> ArtworkView {
        if let artwork { return artwork }
        let created = ArtworkView(size: 0, images: images)
        created.layer.cornerRadius = 9
        created.translatesAutoresizingMaskIntoConstraints = false
        stack.insertArrangedSubview(created, at: 0)
        stack.addArrangedSubview(textStack)
        let aspect = created.heightAnchor.constraint(equalTo: created.widthAnchor, multiplier: 9.0 / 16.0)
        aspect.priority = UILayoutPriority(999)
        aspect.isActive = true
        artAspectConstraint = aspect
        listWidthConstraint = created.widthAnchor.constraint(equalTo: contentView.widthAnchor, multiplier: 0.36)
        artwork = created
        return created
    }

    func configure(item: FilmEpisodeListItem, images: ArtworkImages, list: Bool, theme: PageTheme) {
        let art = ensureArtwork(images: images)
        if isList != list {
            isList = list
            stack.axis = list ? .horizontal : .vertical
            stack.alignment = list ? .center : .fill
            stack.spacing = list ? 12 : 6
            listWidthConstraint?.isActive = list
        }
        let episode = item.episode
        if currentURL != episode.artworkURL {
            currentURL = episode.artworkURL
            art.load(episode.artworkURL)
        }
        heading.text = episode.number.map {
            episode.title.isEmpty ? "第 \($0) 集" : "\($0). \(episode.title)"
        } ?? episode.title
        heading.textAlignment = episode.title.isEmpty && !list ? .center : .natural
        summary.text = episode.summary
        summary.isHidden = episode.summary.isEmpty
        accessibilityIdentifier = item.accessibilityID
        accessibilityLabel = [heading.text, episode.summary].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: "，")
        setItemEnabled(item.enabled)
        applyTheme(theme)
    }

    func setItemEnabled(_ enabled: Bool) {
        contentView.alpha = enabled ? 1 : 0.55
        accessibilityTraits = enabled ? .button : [.button, .notEnabled]
    }

    func applyTheme(_ theme: PageTheme) {
        artwork?.backgroundColor = UIColor(theme.chip)
        heading.textColor = UIColor(theme.ink)
        summary.textColor = UIColor(theme.muted)
    }
}
#endif
