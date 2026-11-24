#if os(iOS)
import UIKit

/// Pure UIKit platform icon badge for music and pan account surfaces.
@MainActor
public final class AccountPlatformMarkView: UIView, PageThemeable {
    private let imageView = UIImageView()
    private let hasAsset: Bool

    public init(assetName: String? = nil, symbol: String = "person.crop.circle",
                size: CGFloat = 34, theme: PageTheme = PageTheme()) {
        let resolvedAsset = assetName.flatMap { UIImage(named: $0) }
        self.hasAsset = resolvedAsset != nil
        super.init(frame: .zero)
        isAccessibilityElement = false
        clipsToBounds = true
        layer.cornerRadius = size * 0.28
        layer.cornerCurve = .continuous
        translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: size),
            heightAnchor.constraint(equalToConstant: size),
        ])
        imageView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(imageView)
        if let image = resolvedAsset {
            imageView.image = image
            imageView.contentMode = .scaleAspectFill
            imageView.pinEdges(to: self)
        } else {
            let config = UIImage.SymbolConfiguration(pointSize: size * 0.45, weight: .regular)
            imageView.image = UIImage(systemName: symbol, withConfiguration: config)
            imageView.contentMode = .center
            NSLayoutConstraint.activate([
                imageView.centerXAnchor.constraint(equalTo: centerXAnchor),
                imageView.centerYAnchor.constraint(equalTo: centerYAnchor),
            ])
        }
        applyTheme(theme)
    }

    @available(*, unavailable)
    public required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public func applyTheme(_ theme: PageTheme) {
        backgroundColor = hasAsset ? .clear : UIColor(theme.settings.input)
        imageView.tintColor = UIColor(theme.settings.accent)
    }
}

/// Pure UIKit capsule input container keeping paired inputs (region + phone, code + send) together.
@MainActor
public final class AccountInputCapsuleView: UIView, PageThemeable {
    public let stack = UIStackView()

    public init(theme: PageTheme = PageTheme()) {
        super.init(frame: .zero)
        layer.borderWidth = 1
        clipsToBounds = true
        stack.axis = .horizontal
        stack.alignment = .center
        stack.spacing = 0
        addSubview(stack)
        stack.pinEdges(to: self)
        heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        applyTheme(theme)
    }

    @available(*, unavailable)
    public required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func layoutSubviews() {
        super.layoutSubviews()
        layer.cornerRadius = bounds.height / 2
    }

    public func applyTheme(_ theme: PageTheme) {
        backgroundColor = UIColor(theme.settings.input)
        layer.borderColor = UIColor(theme.settings.inputBorder).cgColor
    }
}

/// Pure UIKit rounded primary / destructive / secondary action button for floating account cards.
@MainActor
public final class AccountCardButton: UIButton, PageThemeable {
    public enum Style {
        case primary
        case destructive
        case secondary
    }

    public let buttonStyle: Style
    private var currentTheme = PageTheme()

    public init(title: String, style: Style = .primary, theme: PageTheme = PageTheme(),
                action: @escaping @MainActor () -> Void) {
        self.buttonStyle = style
        super.init(frame: .zero)
        setTitle(title, for: .normal)
        titleLabel?.font = .preferredFont(forTextStyle: .body)
        titleLabel?.adjustsFontForContentSizeCategory = true
        titleLabel?.lineBreakMode = .byTruncatingTail
        layer.cornerRadius = 14
        layer.cornerCurve = .continuous
        heightAnchor.constraint(greaterThanOrEqualToConstant: 48).isActive = true
        addAction(UIAction { _ in action() }, for: .touchUpInside)
        applyTheme(theme)
    }

    @available(*, unavailable)
    public required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override var isEnabled: Bool {
        didSet { updateColors() }
    }

    public override var isHighlighted: Bool {
        didSet { alpha = isHighlighted ? 0.85 : 1.0 }
    }

    public func applyTheme(_ theme: PageTheme) {
        currentTheme = theme
        updateColors()
    }

    private func updateColors() {
        let settings = currentTheme.settings
        switch buttonStyle {
        case .primary:
            if isEnabled {
                backgroundColor = UIColor(settings.accent)
                setTitleColor(UIColor(settings.onAccent), for: .normal)
                layer.borderWidth = 0
            } else {
                backgroundColor = UIColor(settings.input)
                setTitleColor(UIColor(settings.muted), for: .normal)
                layer.borderWidth = 1
                layer.borderColor = UIColor(settings.inputBorder).cgColor
            }
        case .destructive:
            backgroundColor = UIColor(settings.danger).withAlphaComponent(isEnabled ? 0.14 : 0.08)
            setTitleColor(UIColor(isEnabled ? settings.danger : settings.muted), for: .normal)
            layer.borderWidth = 1
            layer.borderColor = UIColor(settings.danger).withAlphaComponent(isEnabled ? 0.35 : 0.15).cgColor
        case .secondary:
            backgroundColor = UIColor(settings.input)
            setTitleColor(UIColor(isEnabled ? settings.ink : settings.muted), for: .normal)
            layer.borderWidth = 1
            layer.borderColor = UIColor(settings.inputBorder).cgColor
        }
    }
}
#endif
