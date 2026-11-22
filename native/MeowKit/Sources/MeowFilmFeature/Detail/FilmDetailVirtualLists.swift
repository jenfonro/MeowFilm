#if os(iOS)
import UIKit
import MeowUI

@MainActor
class FilmTouchScrollView: UIScrollView {
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
/// using `UICollectionViewDiffableDataSource` and reusable `FilmEpisodeCell`.
@MainActor
final class FilmEpisodeCollectionView: UIView, UICollectionViewDelegate, UICollectionViewDelegateFlowLayout {
    private let images: ArtworkImages
    private let layout = UICollectionViewFlowLayout()
    let collectionView: UICollectionView
    private var dataSource: UICollectionViewDiffableDataSource<Int, String>!
    private var heightConstraint: NSLayoutConstraint!
    private var itemsByID: [String: FilmEpisodeListItem] = [:]
    private(set) var currentItems: [FilmEpisodeListItem] = []
    private var isListMode = false
    private var viewportWidth: CGFloat = 375
    private var theme: PageTheme

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
            if let self, let item = self.itemsByID[id] {
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

        let oldByID = itemsByID
        var nextByID: [String: FilmEpisodeListItem] = [:]
        var ids: [String] = []
        var toReconfigure: [String] = []
        var seen: Set<String> = []
        for item in items {
            guard seen.insert(item.id).inserted else { continue }
            nextByID[item.id] = item
            ids.append(item.id)
            if let prev = oldByID[item.id],
               layoutChanged || prev.episode != item.episode || prev.enabled != item.enabled ||
               prev.accessibilityID != item.accessibilityID {
                toReconfigure.append(item.id)
            }
        }
        currentItems = items
        itemsByID = nextByID

        var snapshot = NSDiffableDataSourceSnapshot<Int, String>()
        snapshot.appendSections([0])
        snapshot.appendItems(ids, toSection: 0)
        if !toReconfigure.isEmpty { snapshot.reconfigureItems(toReconfigure) }
        dataSource.apply(snapshot, animatingDifferences: false)
        updateHeight(itemCount: ids.count)
    }

    func updateViewportWidth(_ width: CGFloat) {
        let next = max(240, width)
        guard abs(viewportWidth - next) > 0.5 else { return }
        viewportWidth = next
        layout.invalidateLayout()
        updateHeight(itemCount: currentItems.count)
    }

    func updateAvailability(_ available: Bool) {
        var updated: [String] = []
        for (id, var item) in itemsByID {
            let nextEnabled = available && item.onSelect != nil
            if item.enabled != nextEnabled {
                item.enabled = nextEnabled
                itemsByID[id] = item
                updated.append(id)
            }
        }
        if !updated.isEmpty {
            currentItems = currentItems.map { itemsByID[$0.id] ?? $0 }
            for indexPath in collectionView.indexPathsForVisibleItems {
                guard let id = dataSource.itemIdentifier(for: indexPath),
                      let item = itemsByID[id],
                      let cell = collectionView.cellForItem(at: indexPath) as? FilmEpisodeCell else { continue }
                cell.setItemEnabled(item.enabled)
            }
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
              let item = itemsByID[id], item.enabled else { return }
        item.onSelect?()
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

/// Virtualized horizontal cast rail backed by `UICollectionViewDiffableDataSource`.
@MainActor
final class FilmCastRailView: UIView, UICollectionViewDelegateFlowLayout {
    private let images: ArtworkImages
    private let layout = UICollectionViewFlowLayout()
    private let collectionView: UICollectionView
    private let emptyLabel = UILabel()
    private var dataSource: UICollectionViewDiffableDataSource<Int, String>!
    private var membersByID: [String: FilmCastMember] = [:]
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
        collectionView.delegate = self
        collectionView.register(FilmCastCell.self, forCellWithReuseIdentifier: FilmCastCell.reuseIdentifier)
        collectionView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(collectionView)
        collectionView.pinEdges(to: self)

        emptyLabel.text = "暂无演职人员"
        emptyLabel.font = .systemFont(ofSize: 14)
        emptyLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(emptyLabel)
        NSLayoutConstraint.activate([
            emptyLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16),
            emptyLabel.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])

        heightConstraint = heightAnchor.constraint(equalToConstant: 180)
        heightConstraint.isActive = true

        dataSource = UICollectionViewDiffableDataSource<Int, String>(collectionView: collectionView) { [weak self] cv, indexPath, id in
            let cell = cv.dequeueReusableCell(withReuseIdentifier: FilmCastCell.reuseIdentifier, for: indexPath) as! FilmCastCell
            if let self, let member = self.membersByID[id] {
                cell.configure(member: member, images: self.images, theme: self.theme)
            }
            return cell
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func apply(members: [FilmCastMember], theme: PageTheme) {
        self.theme = theme
        emptyLabel.isHidden = !members.isEmpty
        emptyLabel.textColor = UIColor(theme.muted)
        collectionView.isHidden = members.isEmpty
        heightConstraint.constant = members.isEmpty ? 36 : 180

        var nextByID: [String: FilmCastMember] = [:]
        var ids: [String] = []
        for (index, member) in members.enumerated() {
            let id = "\(index):\(member.name):\(member.role)"
            nextByID[id] = member
            ids.append(id)
        }
        membersByID = nextByID
        var snapshot = NSDiffableDataSourceSnapshot<Int, String>()
        snapshot.appendSections([0])
        snapshot.appendItems(ids, toSection: 0)
        dataSource.apply(snapshot, animatingDifferences: false)
    }

    func applyTheme(_ theme: PageTheme) {
        self.theme = theme
        emptyLabel.textColor = UIColor(theme.muted)
        for cell in collectionView.visibleCells {
            (cell as? FilmCastCell)?.applyTheme(theme)
        }
    }

    func cancelVisibleArtworks() {
        for cell in collectionView.visibleCells {
            (cell as? FilmCastCell)?.cancelPendingLoad()
        }
    }

    func resumeVisibleArtworks() {
        for cell in collectionView.visibleCells {
            (cell as? FilmCastCell)?.resumePendingLoad()
        }
    }

    func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                        forItemAt indexPath: IndexPath) {
        (cell as? FilmCastCell)?.cancelPendingLoad()
    }

    func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
                        sizeForItemAt indexPath: IndexPath) -> CGSize {
        CGSize(width: 86, height: 176)
    }
}

@MainActor
private final class FilmCastCell: UICollectionViewCell {
    static let reuseIdentifier = "film.detail.cast.cell"

    private var portrait: ArtworkView?
    private let name = UILabel()
    private let role = UILabel()
    private let stack = UIStackView()
    private var currentURL: URL?

    override init(frame: CGRect) {
        super.init(frame: frame)
        name.font = .systemFont(ofSize: 13)
        name.numberOfLines = 2
        name.textAlignment = .center
        role.font = .systemFont(ofSize: 12)
        role.numberOfLines = 2
        role.textAlignment = .center
        stack.axis = .vertical
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
        currentURL = nil
        portrait?.load(nil)
    }

    func cancelPendingLoad() {
        portrait?.cancelPendingLoad()
    }

    func resumePendingLoad() {
        guard let currentURL else { return }
        portrait?.load(currentURL)
    }

    func configure(member: FilmCastMember, images: ArtworkImages, theme: PageTheme) {
        let art: ArtworkView
        if let existing = portrait {
            art = existing
        } else {
            let created = ArtworkView(size: 0, images: images)
            created.layer.cornerRadius = 10
            created.heightAnchor.constraint(equalTo: created.widthAnchor, multiplier: 1.25).isActive = true
            stack.addArrangedSubview(created)
            stack.addArrangedSubview(name)
            stack.addArrangedSubview(role)
            portrait = created
            art = created
        }
        if currentURL != member.portraitURL {
            currentURL = member.portraitURL
            art.load(member.portraitURL)
        }
        name.text = member.name
        role.text = member.role
        accessibilityLabel = [member.name, member.role].filter { !$0.isEmpty }.joined(separator: "，")
        applyTheme(theme)
    }

    func applyTheme(_ theme: PageTheme) {
        portrait?.backgroundColor = UIColor(theme.chip)
        name.textColor = UIColor(theme.ink)
        role.textColor = UIColor(theme.muted)
    }
}

/// Virtualized horizontal related/similar items rail backed by `UICollectionViewDiffableDataSource`.
@MainActor
final class FilmRelatedRailView: UIView, UICollectionViewDelegate, UICollectionViewDelegateFlowLayout {
    var onSelect: ((FilmItem) -> Void)?
    private let images: ArtworkImages
    private let layout = UICollectionViewFlowLayout()
    private let collectionView: UICollectionView
    private let emptyLabel = UILabel()
    private var dataSource: UICollectionViewDiffableDataSource<Int, String>!
    private var itemsByID: [String: FilmItem] = [:]
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
            if let self, let item = self.itemsByID[id] {
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

        var nextByID: [String: FilmItem] = [:]
        var ids: [String] = []
        var seen: Set<String> = []
        for item in items {
            let id = "\(item.sourceID):\(item.id)"
            guard seen.insert(id).inserted else { continue }
            nextByID[id] = item
            ids.append(id)
        }
        itemsByID = nextByID
        var snapshot = NSDiffableDataSourceSnapshot<Int, String>()
        snapshot.appendSections([0])
        snapshot.appendItems(ids, toSection: 0)
        dataSource.apply(snapshot, animatingDifferences: false)
    }

    func updateViewportWidth(_ width: CGFloat) {
        let next = FilmPosterMetrics(viewportWidth: max(240, width)).width
        guard abs(itemWidth - next) > 0.5 else { return }
        itemWidth = next
        layout.invalidateLayout()
        updateHeight(isEmpty: itemsByID.isEmpty)
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
        guard let id = dataSource.itemIdentifier(for: indexPath), let item = itemsByID[id] else { return }
        onSelect?(item)
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
