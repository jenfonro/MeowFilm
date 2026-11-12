#if os(iOS)
import UIKit
import MeowUI

/// Three complete posters and a small part of the next poster fit in the viewport.
struct FilmPosterMetrics {
    static let inset: CGFloat = 16
    static let spacing: CGFloat = 10
    let width: CGFloat
    var posterHeight: CGFloat { width * 1.5 }
    var height: CGFloat { posterHeight + 39 }

    init(viewportWidth: CGFloat) {
        width = max(1, (viewportWidth - Self.inset - Self.spacing * 3) / 3.2)
    }
}

@MainActor
final class FilmPosterCell: UICollectionViewCell {
    static let reuseIdentifier = "FilmPosterCell"
    private var artwork: ArtworkView?
    private let titleLabel = UILabel()
    private let badge = UILabel(), sourceLabel = UILabel()
    private let badgeContainer = UIView(), sourceContainer = UIView()
    private lazy var feedback = PressFeedback(view: contentView)

    override var isHighlighted: Bool {
        didSet { feedback.setPressed(isHighlighted) }
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        titleLabel.font = .systemFont(ofSize: 13, weight: .medium)
        titleLabel.numberOfLines = 2
        titleLabel.lineBreakMode = .byTruncatingTail
        contentView.addSubview(titleLabel)
        badge.font = .systemFont(ofSize: 10, weight: .bold)
        badge.textColor = .white
        badge.textAlignment = .center
        badge.lineBreakMode = .byTruncatingTail
        badge.accessibilityIdentifier = "film.poster.badge"
        badgeContainer.backgroundColor = UIColor(PageColor(rgb: 0xf97316))
        badgeContainer.layer.cornerRadius = 8
        badgeContainer.layer.shadowColor = UIColor.black.cgColor
        badgeContainer.layer.shadowOpacity = 0.18
        badgeContainer.layer.shadowRadius = 8
        badgeContainer.layer.shadowOffset = CGSize(width: 0, height: 8)
        badgeContainer.accessibilityIdentifier = "film.poster.badge.container"
        sourceLabel.font = .systemFont(ofSize: 10, weight: .medium)
        sourceLabel.textAlignment = .center
        sourceLabel.lineBreakMode = .byTruncatingTail
        sourceLabel.accessibilityIdentifier = "film.poster.label"
        sourceContainer.layer.cornerRadius = 8
        sourceContainer.layer.borderWidth = 1
        sourceContainer.accessibilityIdentifier = "film.poster.label.container"
        badgeContainer.addSubview(badge)
        sourceContainer.addSubview(sourceLabel)
        contentView.addSubview(badgeContainer)
        contentView.addSubview(sourceContainer)
        isAccessibilityElement = true
        accessibilityTraits = .button
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(item: FilmItem, images: ArtworkImages, theme: PageTheme,
                   textBadge: String = "", label: String = "") {
        if artwork == nil {
            let image = ArtworkView(size: 0, images: images)
            image.layer.cornerRadius = 7
            contentView.insertSubview(image, at: 0)
            artwork = image
        }
        artwork?.backgroundColor = UIColor(theme.chip)
        artwork?.load(item.posterURL)
        titleLabel.text = item.title
        titleLabel.textColor = UIColor(theme.ink)
        badge.text = textBadge
        badge.isHidden = textBadge.isEmpty
        badgeContainer.isHidden = textBadge.isEmpty
        sourceLabel.text = label
        sourceLabel.isHidden = label.isEmpty
        sourceContainer.isHidden = label.isEmpty
        sourceLabel.textColor = UIColor(PageColor(rgb: theme.dark ? 0x9ca3af : 0x6b7280))
        sourceContainer.backgroundColor = theme.dark ? UIColor.white.withAlphaComponent(0.06) : UIColor(PageColor(rgb: 0xf8fafc))
        sourceContainer.layer.borderColor = (theme.dark ? UIColor.white.withAlphaComponent(0.1) : UIColor(PageColor(rgb: 0xe2e8f0))).cgColor
        accessibilityLabel = [item.title, textBadge, label].filter { !$0.isEmpty }.joined(separator: "，")
        accessibilityIdentifier = "film.poster.\(item.sourceID).\(item.id)"
        setNeedsLayout()
    }

    override func layoutSubviews() {
        feedback.layoutAtRest { super.layoutSubviews() }
        let height = contentView.bounds.width * 1.5
        artwork?.frame = CGRect(x: 0, y: 0, width: contentView.bounds.width, height: height)
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

    override func prepareForReuse() {
        super.prepareForReuse()
        resetPressFeedback()
        artwork?.load(nil)
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        guard window != nil else { resetPressFeedback(); return }
        var ancestor = superview
        while let view = ancestor {
            (view as? UIScrollView)?.delaysContentTouches = false
            ancestor = view.superview
        }
    }

    func resetPressFeedback() {
        isHighlighted = false
        feedback.reset()
    }
}

@MainActor
final class FilmSectionHeader: UICollectionReusableView {
    static let reuseIdentifier = "FilmSectionHeader"
    private let titleLabel = UILabel()
    private let more = UIButton(type: .system)
    private var onMore: (() -> Void)?

    override init(frame: CGRect) {
        super.init(frame: frame)
        titleLabel.font = .systemFont(ofSize: 18, weight: .semibold)
        titleLabel.accessibilityTraits = .header
        more.setTitle("查看更多", for: .normal)
        more.titleLabel?.font = .systemFont(ofSize: 13)
        more.addAction(UIAction { [weak self] _ in self?.onMore?() }, for: .touchUpInside)
        addSubview(titleLabel)
        addSubview(more)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(title: String, theme: PageTheme, sectionID: String, onMore: (() -> Void)?) {
        titleLabel.text = title
        titleLabel.textColor = UIColor(theme.ink)
        self.onMore = onMore
        more.isHidden = onMore == nil
        more.tintColor = UIColor(theme.muted)
        more.accessibilityIdentifier = "film.home.more.\(sectionID)"
        more.accessibilityLabel = "查看更多\(title)"
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let width: CGFloat = more.isHidden ? 0 : 72
        more.frame = CGRect(x: bounds.width - width, y: 0, width: width, height: bounds.height)
        titleLabel.frame = CGRect(x: 0, y: 8, width: max(0, bounds.width - width), height: bounds.height - 16)
    }
}

@MainActor
final class FilmEmptyCell: UICollectionViewCell {
    static let reuseIdentifier = "FilmEmptyCell"
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
