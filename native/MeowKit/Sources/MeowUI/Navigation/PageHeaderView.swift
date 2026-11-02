#if os(iOS)
import UIKit

/// The visible page header is app-owned. The system navigation bar stays
/// hidden and empty, so appearance and transitions are not constrained by it.
@MainActor
public final class PageHeaderButton: UIButton {
    public override var intrinsicContentSize: CGSize { CGSize(width: 44, height: 44) }
    public override var isHighlighted: Bool {
        get { false }
        set {}
    }

    public init(image: UIImage, accessibilityLabel: String, action: @escaping @MainActor () -> Void) {
        super.init(frame: .zero)
        setImage(image.withRenderingMode(.alwaysTemplate), for: .normal)
        self.accessibilityLabel = accessibilityLabel
        tintAdjustmentMode = .normal
        adjustsImageWhenHighlighted = false
        setContentHuggingPriority(.required, for: .horizontal)
        setContentCompressionResistancePriority(.required, for: .horizontal)
        addAction(UIAction { _ in action() }, for: .touchUpInside)
    }

    public init(image: UIImage, accessibilityLabel: String, menu: UIMenu) {
        super.init(frame: .zero)
        setImage(image.withRenderingMode(.alwaysTemplate), for: .normal)
        self.accessibilityLabel = accessibilityLabel
        tintAdjustmentMode = .normal
        adjustsImageWhenHighlighted = false
        setContentHuggingPriority(.required, for: .horizontal)
        setContentCompressionResistancePriority(.required, for: .horizontal)
        self.menu = menu
        showsMenuAsPrimaryAction = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}

@MainActor
public final class PageHeaderTextButton: UIButton {
    public override var intrinsicContentSize: CGSize {
        CGSize(width: max(44, super.intrinsicContentSize.width), height: 44)
    }
    public override var isHighlighted: Bool {
        get { false }
        set {}
    }

    public init(title: String, accessibilityLabel: String? = nil,
                action: @escaping @MainActor () -> Void) {
        super.init(frame: .zero)
        setTitle(title, for: .normal)
        titleLabel?.font = .systemFont(ofSize: 15, weight: .semibold)
        self.accessibilityLabel = accessibilityLabel ?? title
        tintAdjustmentMode = .normal
        addAction(UIAction { _ in action() }, for: .touchUpInside)
        setContentHuggingPriority(.required, for: .horizontal)
        setContentCompressionResistancePriority(.required, for: .horizontal)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public func applyTheme(_ theme: PageTheme) {
        setTitleColor(UIColor(theme.ink), for: .normal)
        setTitleColor(UIColor(theme.muted), for: .disabled)
        setTitleColor(UIColor(theme.ink), for: .highlighted)
    }
}

@MainActor
public final class PageHeaderView: UIView {
    public let background: WindowGradientView
    public var statusBarHeight: CGFloat = 0 {
        didSet {
            guard statusBarHeight != oldValue else { return }
            rowTop.constant = statusBarHeight
        }
    }
    public var title: String? {
        get { titleLabel.text }
        set {
            titleLabel.text = newValue
            titleLabel.isHidden = newValue == nil || titleContent != nil
            setNeedsLayout()
        }
    }
    public var hasExplicitLeading: Bool { leading != nil }
    public var trailingCount: Int { trailing.count }
    /// Tests and page chrome use the same row geometry without reaching into
    /// the internal stacks.
    public var contentFrame: CGRect {
        row.convert(row.bounds, to: self)
    }

    private let row = UIView()
    private let leadingStack = UIStackView()
    private let trailingStack = UIStackView()
    private let titleLabel = UILabel()
    private let titleContainer = UIView()
    private var rowTop: NSLayoutConstraint!
    private var emptyTrailingWidth: NSLayoutConstraint!
    private var leading: UIView?
    private var titleContent: UIView?
    private var trailing: [UIView] = []

    public init(themeProvider: any PageThemeProviding) {
        background = WindowGradientView(themeProvider: themeProvider)
        super.init(frame: .zero)
        clipsToBounds = true
        backgroundColor = .clear
        background.isUserInteractionEnabled = false
        background.accessibilityElementsHidden = true
        addSubview(background)
        background.pinEdges(to: self)
        addSubview(row)
        row.translatesAutoresizingMaskIntoConstraints = false
        rowTop = row.topAnchor.constraint(equalTo: topAnchor)
        emptyTrailingWidth = trailingStack.widthAnchor.constraint(equalToConstant: 0)
        leadingStack.axis = .horizontal
        leadingStack.alignment = .center
        leadingStack.spacing = 0
        trailingStack.axis = .horizontal
        trailingStack.alignment = .center
        trailingStack.spacing = 0
        for stack in [leadingStack, trailingStack] {
            row.addSubview(stack)
            stack.translatesAutoresizingMaskIntoConstraints = false
            stack.setContentHuggingPriority(.required, for: .horizontal)
            stack.setContentCompressionResistancePriority(.required, for: .horizontal)
        }
        titleLabel.font = .systemFont(ofSize: 17, weight: .semibold)
        titleLabel.textAlignment = .center
        titleLabel.lineBreakMode = .byTruncatingTail
        titleLabel.numberOfLines = 1
        titleLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        titleContainer.addSubview(titleLabel)
        row.addSubview(titleContainer)
        titleContainer.translatesAutoresizingMaskIntoConstraints = false
        titleLabel.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            row.leadingAnchor.constraint(equalTo: safeAreaLayoutGuide.leadingAnchor),
            row.trailingAnchor.constraint(equalTo: safeAreaLayoutGuide.trailingAnchor),
            rowTop,
            row.bottomAnchor.constraint(equalTo: bottomAnchor),
            leadingStack.leadingAnchor.constraint(equalTo: row.leadingAnchor, constant: 8),
            leadingStack.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            leadingStack.heightAnchor.constraint(equalToConstant: 44),
            leadingStack.widthAnchor.constraint(equalToConstant: 44),
            trailingStack.trailingAnchor.constraint(equalTo: row.trailingAnchor, constant: -8),
            trailingStack.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            trailingStack.heightAnchor.constraint(equalToConstant: 44),
            emptyTrailingWidth,
            titleContainer.leadingAnchor.constraint(equalTo: leadingStack.trailingAnchor, constant: 4),
            titleContainer.trailingAnchor.constraint(equalTo: trailingStack.leadingAnchor, constant: -4),
            titleContainer.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            titleContainer.heightAnchor.constraint(equalToConstant: PageLayoutMetrics.searchControl),
            titleLabel.centerXAnchor.constraint(equalTo: row.centerXAnchor),
            titleLabel.centerYAnchor.constraint(equalTo: titleContainer.centerYAnchor),
            titleLabel.leadingAnchor.constraint(greaterThanOrEqualTo: titleContainer.leadingAnchor),
            titleLabel.trailingAnchor.constraint(lessThanOrEqualTo: titleContainer.trailingAnchor),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public func setLeading(_ view: UIView?) {
        guard leading !== view else { return }
        for item in leadingStack.arrangedSubviews {
            leadingStack.removeArrangedSubview(item)
            item.removeFromSuperview()
        }
        leading?.removeFromSuperview()
        leading = view
        setNeedsLayout()
        guard let view else { return }
        view.translatesAutoresizingMaskIntoConstraints = false
        leadingStack.addArrangedSubview(view)
    }

    public func setTitleView(_ view: UIView?) {
        guard titleContent !== view else { return }
        titleContent?.removeFromSuperview()
        titleContent = view
        titleLabel.isHidden = view != nil || titleLabel.text == nil
        setNeedsLayout()
        guard let view else { return }
        titleContainer.addSubview(view)
        view.pinEdges(to: titleContainer)
    }

    public func setTrailing(_ views: [UIView]) {
        guard !trailing.elementsEqual(views, by: { $0 === $1 }) else { return }
        for item in trailingStack.arrangedSubviews {
            trailingStack.removeArrangedSubview(item)
            item.removeFromSuperview()
        }
        trailing = views
        emptyTrailingWidth.isActive = views.isEmpty
        setNeedsLayout()
        for view in views {
            view.translatesAutoresizingMaskIntoConstraints = false
            trailingStack.addArrangedSubview(view)
        }
    }

    public func applyTheme(_ theme: PageTheme) {
        titleLabel.textColor = UIColor(theme.ink)
        for button in [leading, titleContent].compactMap({ $0 }) {
            (button as? UIButton)?.tintColor = UIColor(theme.ink)
            for child in button.subviews.compactMap({ $0 as? UIButton }) {
                child.tintColor = UIColor(theme.ink)
            }
        }
        for button in trailing {
            (button as? UIButton)?.tintColor = UIColor(theme.ink)
            (button as? PageHeaderTextButton)?.applyTheme(theme)
            for child in button.subviews.compactMap({ $0 as? UIButton }) {
                child.tintColor = UIColor(theme.ink)
            }
        }
        background.applyTheme()
    }

}
#endif
