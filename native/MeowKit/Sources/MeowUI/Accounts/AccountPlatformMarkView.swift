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
public final class AccountInputCapsuleView: UIStackView, PageThemeable {
    public var stack: UIStackView { self }

    public init(theme: PageTheme = PageTheme()) {
        super.init(frame: .zero)
        layer.borderWidth = 1
        clipsToBounds = true
        axis = .horizontal
        alignment = .center
        spacing = 0
        heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        applyTheme(theme)
    }

    @available(*, unavailable)
    public required init(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

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
        self.currentTheme = theme
        super.init(frame: .zero)
        var config = UIButton.Configuration.filled()
        config.title = title
        config.titleLineBreakMode = .byTruncatingTail
        config.cornerStyle = .fixed
        config.background.cornerRadius = 14
        config.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { [weak self] incoming in
            var attrs = incoming
            attrs.font = .preferredFont(forTextStyle: .body, compatibleWith: self?.traitCollection)
            return attrs
        }
        configuration = config
        configurationUpdateHandler = { [weak self] button in
            self?.refreshConfiguration(button)
        }
        heightAnchor.constraint(greaterThanOrEqualToConstant: 48).isActive = true
        addAction(UIAction { _ in action() }, for: .touchUpInside)
        setNeedsUpdateConfiguration()
    }

    @available(*, unavailable)
    public required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public func applyTheme(_ theme: PageTheme) {
        currentTheme = theme
        setNeedsUpdateConfiguration()
    }

    private func refreshConfiguration(_ button: UIButton) {
        guard var config = button.configuration else { return }
        let settings = currentTheme.settings
        let enabled = button.isEnabled
        switch buttonStyle {
        case .primary:
            config.baseBackgroundColor = UIColor(enabled ? settings.accent : settings.input)
            config.baseForegroundColor = UIColor(enabled ? settings.onAccent : settings.muted)
            config.background.strokeWidth = enabled ? 0 : 1
            config.background.strokeColor = UIColor(settings.inputBorder)
        case .destructive:
            config.baseBackgroundColor = UIColor(settings.danger).withAlphaComponent(enabled ? 0.14 : 0.08)
            config.baseForegroundColor = UIColor(enabled ? settings.danger : settings.muted)
            config.background.strokeWidth = 1
            config.background.strokeColor = UIColor(settings.danger).withAlphaComponent(enabled ? 0.35 : 0.15)
        case .secondary:
            config.baseBackgroundColor = UIColor(settings.input)
            config.baseForegroundColor = UIColor(enabled ? settings.ink : settings.muted)
            config.background.strokeWidth = 1
            config.background.strokeColor = UIColor(settings.inputBorder)
        }
        button.alpha = button.isHighlighted ? 0.85 : 1.0
        button.configuration = config
    }
}
#endif
