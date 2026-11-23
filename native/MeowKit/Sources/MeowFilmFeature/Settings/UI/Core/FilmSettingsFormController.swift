#if os(iOS)
import UIKit
import MeowUI

@MainActor
extension SettingsFormViewController {
    func filmOption(_ title: String, id: String, disclosure: Bool = false, destructive: Bool = false,
                    action: @escaping () -> Void) -> UIButton {
        let button = FilmSettingsOptionButton(destructive: destructive)
        var configuration = UIButton.Configuration.plain()
        configuration.title = title
        configuration.titleLineBreakMode = .byWordWrapping
        configuration.contentInsets = .init(top: 8, leading: 12, bottom: 8, trailing: 12)
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { [weak button] attributes in
            var attributes = attributes
            attributes.font = UIFont.preferredFont(forTextStyle: .subheadline, compatibleWith: button?.traitCollection)
            return attributes
        }
        if disclosure {
            configuration.image = UIImage(systemName: "chevron.up.chevron.down",
                withConfiguration: UIImage.SymbolConfiguration(pointSize: 9, weight: .regular))
            configuration.imagePlacement = .trailing; configuration.imagePadding = 8
        }
        button.configuration = configuration
        button.titleLabel?.numberOfLines = 0; button.titleLabel?.adjustsFontForContentSizeCategory = true
        button.contentHorizontalAlignment = .leading
        button.setContentCompressionResistancePriority(.required, for: .vertical)
        button.accessibilityIdentifier = id; button.accessibilityLabel = title
        button.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        button.addAction(UIAction { _ in action() }, for: .touchUpInside)
        registerThemeable(button)
        return button
    }

    func filmOptionRow(_ controls: [UIView]) -> UIStackView {
        for control in controls {
            if let themeable = control as? any PageThemeable {
                registerThemeable(themeable)
            }
        }
        let row = UIStackView(arrangedSubviews: controls)
        row.spacing = 8; row.isLayoutMarginsRelativeArrangement = true
        row.layoutMargins = .init(top: 6, left: 14, bottom: 6, right: 14)
        return row
    }
}

@MainActor
private final class FilmSettingsOptionButton: UIButton, PageThemeable {
    private let destructive: Bool
    private var fittingHeight: CGFloat = 0

    init(destructive: Bool) {
        self.destructive = destructive
        super.init(frame: .zero)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func applyTheme(_ theme: PageTheme) {
        configuration?.baseForegroundColor = UIColor(destructive ? theme.settings.danger : theme.settings.accent)
    }

    override var intrinsicContentSize: CGSize {
        let size = super.intrinsicContentSize
        guard let titleLabel, titleLabel.preferredMaxLayoutWidth > 0 else { return size }
        let titleSize = titleLabel.sizeThatFits(.init(width: titleLabel.preferredMaxLayoutWidth, height: .greatestFiniteMagnitude))
        let insets = configuration?.contentInsets ?? .zero
        let height = max(titleSize.height, configuration?.image?.size.height ?? 0) + insets.top + insets.bottom
        return .init(width: size.width, height: max(size.height, ceil(height)))
    }

    override func layoutSubviews() {
        let insets = configuration?.contentInsets ?? .zero
        let imageWidth = configuration?.image.map { $0.size.width + (configuration?.imagePadding ?? 0) } ?? 0
        let width = max(0, bounds.width - insets.leading - insets.trailing - imageWidth)
        if titleLabel?.preferredMaxLayoutWidth != width {
            titleLabel?.preferredMaxLayoutWidth = width; invalidateIntrinsicContentSize()
        }
        super.layoutSubviews()
        let height = intrinsicContentSize.height
        if fittingHeight != height { fittingHeight = height; invalidateIntrinsicContentSize() }
    }
}
#endif
