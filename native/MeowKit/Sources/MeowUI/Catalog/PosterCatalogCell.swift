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

public struct PosterCatalogArtwork {
    public let id: AnyHashable?
    public let placeholderSymbol: String?
    public let cached: @MainActor () -> UIImage?
    public let load: @MainActor () async -> UIImage?

    public init(
        id: AnyHashable?,
        placeholderSymbol: String? = nil,
        cached: @escaping @MainActor () -> UIImage?,
        load: @escaping @MainActor () async -> UIImage?
    ) {
        self.id = id
        self.placeholderSymbol = placeholderSymbol
        self.cached = cached
        self.load = load
    }

    public static func url(_ url: URL?, images: ArtworkImages) -> Self {
        Self(
            id: url.map { AnyHashable($0) },
            cached: { url.flatMap { images.cached($0) } },
            load: {
                guard let url else { return nil }
                return try? await images.read(url).0
            }
        )
    }
}

public struct PosterCatalogCard: Equatable {
    public let id: String
    public let title: String
    public let textBadge: String
    public let label: String
    public let accessibilityIdentifier: String
    public let artwork: PosterCatalogArtwork

    public init(
        id: String,
        title: String,
        textBadge: String = "",
        label: String = "",
        accessibilityIdentifier: String,
        artwork: PosterCatalogArtwork
    ) {
        self.id = id
        self.title = title
        self.textBadge = textBadge
        self.label = label
        self.accessibilityIdentifier = accessibilityIdentifier
        self.artwork = artwork
    }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.id == rhs.id && lhs.title == rhs.title && lhs.textBadge == rhs.textBadge &&
            lhs.label == rhs.label && lhs.accessibilityIdentifier == rhs.accessibilityIdentifier &&
            lhs.artwork.id == rhs.artwork.id
    }
}

public struct PosterCatalogSection: Equatable {
    public let id: String
    public let title: String
    public let hasMore: Bool
    public let actionTitle: String?
    public let isLoading: Bool
    public let statusText: String
    public let items: [PosterCatalogCard]

    public init(
        id: String,
        title: String,
        hasMore: Bool,
        actionTitle: String? = nil,
        isLoading: Bool = false,
        statusText: String,
        items: [PosterCatalogCard]
    ) {
        self.id = id
        self.title = title
        self.hasMore = hasMore
        self.actionTitle = actionTitle
        self.isLoading = isLoading
        self.statusText = statusText
        self.items = items
    }
}

@MainActor
public final class PosterCatalogCell: UICollectionViewCell, PageThemeable {
    public static let reuseIdentifier = "PosterCatalogCell"

    private let posterContainer = UIView()
    private let imageView = UIImageView()
    private let placeholder = UIImageView()
    private var artworkSpec: PosterCatalogArtwork?
    private var loadToken = UUID()
    private nonisolated(unsafe) var pendingLoad: Task<Void, Never>?

    private let titleLabel = UILabel()
    private let badge = UILabel(), sourceLabel = UILabel()
    private let badgeContainer = UIView(), sourceContainer = UIView()
    private lazy var feedback = PressFeedback(view: contentView)

    public override var isHighlighted: Bool {
        didSet { feedback.setPressed(isHighlighted) }
    }

    public override init(frame: CGRect) {
        super.init(frame: frame)
        posterContainer.clipsToBounds = true
        posterContainer.layer.cornerRadius = 7
        imageView.contentMode = .scaleAspectFill
        imageView.clipsToBounds = true
        placeholder.contentMode = .scaleAspectFit
        placeholder.preferredSymbolConfiguration = UIImage.SymbolConfiguration(pointSize: 24, weight: .regular)
        posterContainer.addSubview(imageView)
        posterContainer.addSubview(placeholder)
        contentView.addSubview(posterContainer)

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
        let spec = card.artwork
        placeholder.image = spec.placeholderSymbol.flatMap { $0.isEmpty ? nil : UIImage(systemName: $0) }
        configureArtwork(spec, loadImmediately: loadArtwork)
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

    private func configureArtwork(_ spec: PosterCatalogArtwork, loadImmediately: Bool) {
        guard let id = spec.id else {
            cancelPendingLoad()
            artworkSpec = nil
            imageView.image = nil
            placeholder.isHidden = placeholder.image == nil
            return
        }
        let sameID = artworkSpec?.id == id
        artworkSpec = spec
        if sameID, imageView.image != nil || (pendingLoad != nil && loadImmediately) { return }
        cancelPendingLoad()
        if let image = spec.cached() {
            imageView.image = image
            placeholder.isHidden = true
            return
        }
        imageView.image = nil
        placeholder.isHidden = placeholder.image == nil
        if loadImmediately { startLoad(expectedID: id, loader: spec.load) }
    }

    private func startLoad(expectedID: AnyHashable, loader: @escaping @MainActor () async -> UIImage?) {
        let token = UUID()
        loadToken = token
        pendingLoad = Task { [weak self] in
            let image = await loader()
            guard !Task.isCancelled, let self, self.loadToken == token, self.artworkSpec?.id == expectedID else { return }
            self.pendingLoad = nil
            guard let image else { return }
            if self.imageView.image == nil {
                self.imageView.image = image
            } else {
                UIView.transition(with: self.imageView, duration: 0.2, options: .transitionCrossDissolve) {
                    self.imageView.image = image
                }
            }
            self.placeholder.isHidden = true
        }
    }

    public func applyTheme(_ theme: PageTheme) {
        posterContainer.backgroundColor = UIColor(theme.chip)
        placeholder.tintColor = UIColor(theme.muted.mixed(with: theme.ink, ratio: 0.25))
        titleLabel.textColor = UIColor(theme.ink)
        sourceLabel.textColor = UIColor(PageColor(rgb: theme.dark ? 0x9ca3af : 0x6b7280))
        sourceContainer.backgroundColor = theme.dark ? UIColor.white.withAlphaComponent(0.06) : UIColor(PageColor(rgb: 0xf8fafc))
        sourceContainer.layer.borderColor = (theme.dark ? UIColor.white.withAlphaComponent(0.1) : UIColor(PageColor(rgb: 0xe2e8f0))).cgColor
    }

    public override func layoutSubviews() {
        feedback.layoutAtRest { super.layoutSubviews() }
        let height = contentView.bounds.width * 1.5
        posterContainer.frame = CGRect(x: 0, y: 0, width: contentView.bounds.width, height: height)
        imageView.frame = posterContainer.bounds
        if !placeholder.isHidden {
            placeholder.frame = CGRect(x: (posterContainer.bounds.width - 28) / 2,
                                       y: (posterContainer.bounds.height - 28) / 2, width: 28, height: 28)
        }
        titleLabel.frame = CGRect(x: 0, y: height + 7, width: contentView.bounds.width, height: 32)
        if !badgeContainer.isHidden {
            let badgeWidth = min(contentView.bounds.width * 0.7, ceil(badge.intrinsicContentSize.width) + 16)
            badgeContainer.frame = CGRect(x: contentView.bounds.width - badgeWidth - 8, y: 8, width: badgeWidth, height: 19)
            badge.frame = badgeContainer.bounds.insetBy(dx: 8, dy: 2)
            badgeContainer.layer.shadowPath = UIBezierPath(roundedRect: badgeContainer.bounds, cornerRadius: 8).cgPath
        }
        if !sourceContainer.isHidden {
            let sourceWidth = min(contentView.bounds.width, ceil(sourceLabel.intrinsicContentSize.width) + 18)
            sourceContainer.frame = CGRect(x: (contentView.bounds.width - sourceWidth) / 2, y: height + 41, width: sourceWidth, height: 18)
            sourceLabel.frame = sourceContainer.bounds.insetBy(dx: 9, dy: 3)
        }
    }

    public override func prepareForReuse() {
        super.prepareForReuse()
        resetPressFeedback()
        cancelPendingLoad()
        artworkSpec = nil
        imageView.image = nil
        placeholder.isHidden = false
    }

    public func cancelPendingLoad() {
        loadToken = UUID()
        pendingLoad?.cancel()
        pendingLoad = nil
    }

    public func resumePendingLoad() {
        guard let spec = artworkSpec, let id = spec.id, imageView.image == nil, pendingLoad == nil else { return }
        if let image = spec.cached() {
            imageView.image = image
            placeholder.isHidden = true
            return
        }
        startLoad(expectedID: id, loader: spec.load)
    }

    public override func didMoveToWindow() {
        super.didMoveToWindow()
        if window == nil { resetPressFeedback() }
    }

    public func resetPressFeedback() {
        isHighlighted = false
        feedback.reset()
    }

    nonisolated deinit { pendingLoad?.cancel() }
}

@MainActor
final class PosterSectionHeaderView: UICollectionReusableView {
    static let reuseIdentifier = "PosterSectionHeaderView"
    private static let chevronImage = UIImage(
        systemName: "chevron.forward",
        withConfiguration: UIImage.SymbolConfiguration(pointSize: 12, weight: .regular)
    )
    private let titleButton = UIButton(type: .system)
    private let actionButton = UIButton(type: .system)
    private var onMore: (() -> Void)?
    private var onAction: (() -> Void)?

    override init(frame: CGRect) {
        super.init(frame: frame)
        var configuration = UIButton.Configuration.plain()
        configuration.imagePlacement = .trailing
        configuration.imagePadding = 8
        configuration.contentInsets = .zero
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var attributes = $0; attributes.font = .systemFont(ofSize: 18); return attributes
        }
        titleButton.configuration = configuration
        titleButton.contentHorizontalAlignment = .leading
        titleButton.titleLabel?.lineBreakMode = .byTruncatingTail
        titleButton.accessibilityTraits.insert(.header)
        titleButton.addAction(UIAction { [weak self] _ in self?.onMore?() }, for: .touchUpInside)

        actionButton.titleLabel?.font = .systemFont(ofSize: 13)
        actionButton.contentHorizontalAlignment = .trailing
        actionButton.addAction(UIAction { [weak self] _ in self?.onAction?() }, for: .touchUpInside)

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
        self.onMore = onMore
        self.onAction = onAction
        var configuration = titleButton.configuration ?? .plain()
        configuration.title = title
        configuration.image = onMore == nil ? nil : Self.chevronImage
        configuration.baseForegroundColor = UIColor(theme.ink)
        titleButton.configuration = configuration
        titleButton.isUserInteractionEnabled = onMore != nil
        titleButton.accessibilityTraits = onMore == nil ? .header : [.button, .header]
        titleButton.accessibilityIdentifier = onMore == nil ? nil : moreAccessibilityID
        titleButton.accessibilityLabel = onMore == nil ? title : "查看更多\(title)"

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
        let width = onMore == nil ? availableWidth : min(availableWidth, ceil(titleButton.intrinsicContentSize.width))
        titleButton.frame = CGRect(x: 0, y: 0, width: width, height: bounds.height)
    }
}

@MainActor
final class PosterEmptyCell: UICollectionViewCell {
    static let reuseIdentifier = "PosterEmptyCell"
    let label = UILabel()
    let activity = UIActivityIndicatorView(style: .medium)

    override init(frame: CGRect) {
        super.init(frame: frame)
        label.font = .systemFont(ofSize: 13)
        label.textAlignment = .center
        activity.hidesWhenStopped = true
        contentView.addSubview(label)
        contentView.addSubview(activity)
        label.pinEdges(to: contentView)
        activity.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            activity.centerXAnchor.constraint(equalTo: contentView.centerXAnchor),
            activity.centerYAnchor.constraint(equalTo: contentView.centerYAnchor),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
#endif
