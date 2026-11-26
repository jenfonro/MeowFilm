#if os(iOS)
import UIKit

/// Three complete posters and a small part of the next poster fit in the viewport.
public struct PosterCatalogMetrics: Sendable {
    public static let inset: CGFloat = 16
    public static let spacing: CGFloat = 10
    public let width: CGFloat
    public var posterHeight: CGFloat { width * 1.5 }
    public var height: CGFloat { posterHeight + 39 }

    public init(viewportWidth: CGFloat) {
        width = max(1, (viewportWidth - Self.inset - Self.spacing * 3) / 3.2)
    }
}

public enum PosterCatalogArtwork {
    case url(URL?, ArtworkImages)
    case custom(
        id: AnyHashable?,
        placeholderSymbol: String?,
        cached: @MainActor () -> UIImage?,
        load: @MainActor () async -> UIImage?
    )
}

public struct PosterCatalogCard: Equatable {
    public let id: String
    public let title: String
    public let textBadge: String
    public let label: String
    public let accessibilityIdentifier: String
    public let artworkIdentity: AnyHashable?
    public let artwork: PosterCatalogArtwork

    public init(
        id: String,
        title: String,
        textBadge: String = "",
        label: String = "",
        accessibilityIdentifier: String,
        artworkIdentity: AnyHashable?,
        artwork: PosterCatalogArtwork
    ) {
        self.id = id
        self.title = title
        self.textBadge = textBadge
        self.label = label
        self.accessibilityIdentifier = accessibilityIdentifier
        self.artworkIdentity = artworkIdentity
        self.artwork = artwork
    }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.id == rhs.id &&
            lhs.title == rhs.title &&
            lhs.textBadge == rhs.textBadge &&
            lhs.label == rhs.label &&
            lhs.accessibilityIdentifier == rhs.accessibilityIdentifier &&
            lhs.artworkIdentity == rhs.artworkIdentity
    }
}

public struct PosterCatalogSection: Equatable {
    public let id: String
    public let title: String
    public let hasMore: Bool
    public let actionTitle: String?
    public let statusText: String
    public let items: [PosterCatalogCard]

    public init(
        id: String,
        title: String,
        hasMore: Bool,
        actionTitle: String? = nil,
        statusText: String,
        items: [PosterCatalogCard]
    ) {
        self.id = id
        self.title = title
        self.hasMore = hasMore
        self.actionTitle = actionTitle
        self.statusText = statusText
        self.items = items
    }
}

@MainActor
public final class PosterCatalogCell: UICollectionViewCell, PageThemeable {
    public static let reuseIdentifier = "PosterCatalogCell"

    private var artwork: ArtworkView?
    private var currentPosterURL: URL?

    private let customContainer = UIView()
    private let customImageView = UIImageView()
    private let customPlaceholder = UIImageView()
    private var customToken = UUID()
    private var representedCustomID: AnyHashable?
    private var customCached: (@MainActor () -> UIImage?)?
    private var customLoader: (@MainActor () async -> UIImage?)?
    private nonisolated(unsafe) var customPending: Task<Void, Never>?

    private let titleLabel = UILabel()
    private let badge = UILabel(), sourceLabel = UILabel()
    private let badgeContainer = UIView(), sourceContainer = UIView()
    private lazy var feedback = PressFeedback(view: contentView)

    public override var isHighlighted: Bool {
        didSet { feedback.setPressed(isHighlighted) }
    }

    public override init(frame: CGRect) {
        super.init(frame: frame)
        customContainer.clipsToBounds = true
        customContainer.layer.cornerRadius = 7
        customContainer.isHidden = true
        customImageView.contentMode = .scaleAspectFill
        customImageView.clipsToBounds = true
        customPlaceholder.contentMode = .scaleAspectFit
        customPlaceholder.preferredSymbolConfiguration = UIImage.SymbolConfiguration(pointSize: 24, weight: .regular)
        customContainer.addSubview(customImageView)
        customContainer.addSubview(customPlaceholder)
        contentView.addSubview(customContainer)

        titleLabel.font = .systemFont(ofSize: 13)
        titleLabel.numberOfLines = 2
        titleLabel.lineBreakMode = .byTruncatingTail
        contentView.addSubview(titleLabel)
        badge.font = .systemFont(ofSize: 10)
        badge.textColor = .white
        badge.textAlignment = .center
        badge.lineBreakMode = .byTruncatingTail
        badge.accessibilityIdentifier = "poster.badge"
        badgeContainer.backgroundColor = UIColor(PageColor(rgb: 0xf97316))
        badgeContainer.layer.cornerRadius = 8
        badgeContainer.layer.shadowColor = UIColor.black.cgColor
        badgeContainer.layer.shadowOpacity = 0.18
        badgeContainer.layer.shadowRadius = 8
        badgeContainer.layer.shadowOffset = CGSize(width: 0, height: 8)
        badgeContainer.accessibilityIdentifier = "poster.badge.container"
        sourceLabel.font = .systemFont(ofSize: 10)
        sourceLabel.textAlignment = .center
        sourceLabel.lineBreakMode = .byTruncatingTail
        sourceLabel.accessibilityIdentifier = "poster.label"
        sourceContainer.layer.cornerRadius = 8
        sourceContainer.layer.borderWidth = 1
        sourceContainer.accessibilityIdentifier = "poster.label.container"
        badgeContainer.addSubview(badge)
        sourceContainer.addSubview(sourceLabel)
        contentView.addSubview(badgeContainer)
        contentView.addSubview(sourceContainer)
        isAccessibilityElement = true
        accessibilityTraits = .button
    }

    @available(*, unavailable)
    public required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public func configure(card: PosterCatalogCard, theme: PageTheme, loadArtwork: Bool = true) {
        switch card.artwork {
        case .url(let url, let images):
            cancelCustomPending()
            representedCustomID = nil
            customCached = nil
            customLoader = nil
            customImageView.image = nil
            customContainer.isHidden = true
            if artwork == nil {
                let image = ArtworkView(size: 0, images: images)
                image.layer.cornerRadius = 7
                contentView.insertSubview(image, at: 0)
                artwork = image
            }
            artwork?.isHidden = false
            currentPosterURL = url
            if loadArtwork {
                artwork?.load(url)
            } else {
                artwork?.cancelPendingLoad()
            }

        case .custom(let id, let symbol, let cached, let load):
            currentPosterURL = nil
            artwork?.load(nil)
            artwork?.isHidden = true
            customContainer.isHidden = false
            if let symbol, !symbol.isEmpty {
                customPlaceholder.image = UIImage(systemName: symbol)
            } else {
                customPlaceholder.image = nil
            }
            configureCustomArtwork(id: id, cached: cached, load: load, loadImmediately: loadArtwork)
        }

        titleLabel.text = card.title
        badge.text = card.textBadge
        badge.isHidden = card.textBadge.isEmpty
        badgeContainer.isHidden = card.textBadge.isEmpty
        sourceLabel.text = card.label
        sourceLabel.isHidden = card.label.isEmpty
        sourceContainer.isHidden = card.label.isEmpty
        applyTheme(theme)
        accessibilityLabel = [card.title, card.textBadge, card.label].filter { !$0.isEmpty }.joined(separator: "，")
        accessibilityIdentifier = card.accessibilityIdentifier
        setNeedsLayout()
    }

    private func configureCustomArtwork(
        id: AnyHashable?,
        cached: @escaping @MainActor () -> UIImage?,
        load: @escaping @MainActor () async -> UIImage?,
        loadImmediately: Bool
    ) {
        guard let id else {
            cancelCustomPending()
            representedCustomID = nil
            customCached = nil
            customLoader = nil
            customImageView.image = nil
            customPlaceholder.isHidden = customPlaceholder.image == nil
            return
        }
        customCached = cached
        customLoader = load
        if representedCustomID == id, customImageView.image != nil || (customPending != nil && loadImmediately) {
            return
        }
        cancelCustomPending()
        representedCustomID = id
        if let image = cached() {
            customImageView.image = image
            customPlaceholder.isHidden = true
            return
        }
        customImageView.image = nil
        customPlaceholder.isHidden = customPlaceholder.image == nil
        if loadImmediately {
            startCustomLoad(expectedID: id)
        }
    }

    private func startCustomLoad(expectedID: AnyHashable) {
        guard let loader = customLoader else { return }
        let token = UUID()
        customToken = token
        customPending = Task { [weak self] in
            let image = await loader()
            guard !Task.isCancelled, let self,
                  self.customToken == token,
                  self.representedCustomID == expectedID else { return }
            self.customPending = nil
            if let image {
                self.customImageView.image = image
                self.customPlaceholder.isHidden = true
            }
        }
    }

    private func cancelCustomPending() {
        customToken = UUID()
        customPending?.cancel()
        customPending = nil
    }

    public func applyTheme(_ theme: PageTheme) {
        artwork?.backgroundColor = UIColor(theme.chip)
        customContainer.backgroundColor = UIColor(theme.chip)
        customPlaceholder.tintColor = UIColor(theme.muted.mixed(with: theme.ink, ratio: 0.25))
        titleLabel.textColor = UIColor(theme.ink)
        sourceLabel.textColor = UIColor(PageColor(rgb: theme.dark ? 0x9ca3af : 0x6b7280))
        sourceContainer.backgroundColor = theme.dark ? UIColor.white.withAlphaComponent(0.06) : UIColor(PageColor(rgb: 0xf8fafc))
        sourceContainer.layer.borderColor = (theme.dark ? UIColor.white.withAlphaComponent(0.1) : UIColor(PageColor(rgb: 0xe2e8f0))).cgColor
    }

    public override func layoutSubviews() {
        feedback.layoutAtRest { super.layoutSubviews() }
        let height = contentView.bounds.width * 1.5
        let posterFrame = CGRect(x: 0, y: 0, width: contentView.bounds.width, height: height)
        artwork?.frame = posterFrame
        customContainer.frame = posterFrame
        customImageView.frame = customContainer.bounds
        customPlaceholder.frame = CGRect(
            x: (customContainer.bounds.width - 28) / 2,
            y: (customContainer.bounds.height - 28) / 2,
            width: 28,
            height: 28
        )
        titleLabel.frame = CGRect(x: 0, y: height + 7, width: contentView.bounds.width, height: 32)
        let badgeWidth = min(contentView.bounds.width * 0.7, ceil(badge.intrinsicContentSize.width) + 16)
        badgeContainer.frame = CGRect(x: contentView.bounds.width - badgeWidth - 8, y: 8,
                                      width: badgeWidth, height: 19)
        badge.frame = badgeContainer.bounds.insetBy(dx: 8, dy: 2)
        badgeContainer.layer.shadowPath = UIBezierPath(roundedRect: badgeContainer.bounds, cornerRadius: 8).cgPath
        let sourceWidth = min(contentView.bounds.width, ceil(sourceLabel.intrinsicContentSize.width) + 18)
        sourceContainer.frame = CGRect(x: (contentView.bounds.width - sourceWidth) / 2, y: height + 41,
                                       width: sourceWidth, height: 18)
        sourceLabel.frame = sourceContainer.bounds.insetBy(dx: 9, dy: 3)
    }

    public override func prepareForReuse() {
        super.prepareForReuse()
        resetPressFeedback()
        currentPosterURL = nil
        artwork?.load(nil)
        cancelCustomPending()
        representedCustomID = nil
        customCached = nil
        customLoader = nil
        customImageView.image = nil
        customPlaceholder.isHidden = false
    }

    public func cancelPendingLoad() {
        artwork?.cancelPendingLoad()
        cancelCustomPending()
    }

    public func resumePendingLoad() {
        if let url = currentPosterURL {
            artwork?.load(url)
            return
        }
        guard let id = representedCustomID, customImageView.image == nil, customPending == nil else { return }
        if let image = customCached?() {
            customImageView.image = image
            customPlaceholder.isHidden = true
            return
        }
        startCustomLoad(expectedID: id)
    }

    public override func didMoveToWindow() {
        super.didMoveToWindow()
        guard window != nil else { resetPressFeedback(); return }
        var ancestor = superview
        while let view = ancestor {
            (view as? UIScrollView)?.delaysContentTouches = false
            ancestor = view.superview
        }
    }

    public func resetPressFeedback() {
        isHighlighted = false
        feedback.reset()
    }

    nonisolated deinit {
        customPending?.cancel()
    }
}

@MainActor
final class PosterSectionHeaderView: UICollectionReusableView {
    static let reuseIdentifier = "PosterSectionHeaderView"
    private let titleLabel = UILabel()
    private let titleButton = UIButton(type: .system)
    private let actionButton = UIButton(type: .system)
    private var onMore: (() -> Void)?
    private var onAction: (() -> Void)?

    override init(frame: CGRect) {
        super.init(frame: frame)
        titleLabel.font = .systemFont(ofSize: 18)
        titleLabel.accessibilityTraits = .header
        var configuration = UIButton.Configuration.plain()
        configuration.image = UIImage(systemName: "chevron.forward")
        configuration.preferredSymbolConfigurationForImage = UIImage.SymbolConfiguration(pointSize: 12, weight: .regular)
        configuration.imagePlacement = .trailing
        configuration.imagePadding = 8
        configuration.contentInsets = .zero
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var attributes = $0
            attributes.font = .systemFont(ofSize: 18)
            return attributes
        }
        titleButton.configuration = configuration
        titleButton.contentHorizontalAlignment = .leading
        titleButton.titleLabel?.lineBreakMode = .byTruncatingTail
        titleButton.accessibilityTraits.insert(.header)
        titleButton.addAction(UIAction { [weak self] _ in self?.onMore?() }, for: .touchUpInside)

        actionButton.titleLabel?.font = .systemFont(ofSize: 13)
        actionButton.contentHorizontalAlignment = .trailing
        actionButton.addAction(UIAction { [weak self] _ in self?.onAction?() }, for: .touchUpInside)

        addSubview(titleLabel)
        addSubview(titleButton)
        addSubview(actionButton)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        onMore = nil
        onAction = nil
        titleButton.accessibilityIdentifier = nil
        titleButton.accessibilityLabel = nil
        actionButton.accessibilityIdentifier = nil
        actionButton.accessibilityLabel = nil
    }

    func configure(
        title: String,
        theme: PageTheme,
        moreAccessibilityID: String,
        onMore: (() -> Void)?,
        actionTitle: String? = nil,
        onAction: (() -> Void)? = nil
    ) {
        titleLabel.text = title
        titleLabel.textColor = UIColor(theme.ink)
        self.onMore = onMore
        self.onAction = onAction
        titleLabel.isHidden = onMore != nil
        titleButton.isHidden = onMore == nil
        titleButton.configuration?.title = title
        titleButton.configuration?.baseForegroundColor = UIColor(theme.ink)
        titleButton.accessibilityIdentifier = moreAccessibilityID
        titleButton.accessibilityLabel = "查看更多\(title)"

        let hasAction = actionTitle != nil && onAction != nil
        actionButton.isHidden = !hasAction
        actionButton.setTitle(actionTitle, for: .normal)
        actionButton.setTitleColor(UIColor(theme.muted), for: .normal)
        actionButton.accessibilityIdentifier = hasAction ? "\(moreAccessibilityID).action" : nil
        actionButton.accessibilityLabel = actionTitle
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let actionWidth: CGFloat = actionButton.isHidden
            ? 0
            : max(44, ceil(actionButton.intrinsicContentSize.width) + 8)
        actionButton.frame = CGRect(x: max(0, bounds.width - actionWidth), y: 0, width: actionWidth, height: bounds.height)
        let availableWidth = max(0, bounds.width - (actionWidth > 0 ? actionWidth + 8 : 0))
        let width = min(availableWidth, ceil(titleButton.intrinsicContentSize.width))
        titleButton.frame = CGRect(x: 0, y: 0, width: width, height: bounds.height)
        titleLabel.frame = CGRect(x: 0, y: 8, width: availableWidth, height: bounds.height - 16)
    }
}

@MainActor
final class PosterEmptyCell: UICollectionViewCell {
    static let reuseIdentifier = "PosterEmptyCell"
    let label = UILabel()

    override init(frame: CGRect) {
        super.init(frame: frame)
        label.font = .systemFont(ofSize: 13)
        contentView.addSubview(label)
        label.pinEdges(to: contentView)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
#endif
