#if os(iOS)
import UIKit

/// A reusable settings row with shared typography, spacing, surface, and divider rules.
@MainActor
public final class SettingsTableCell: UITableViewCell {
    public static let reuseIdentifier = "meow.settings.cell"

    private let normalBackground = SettingsRowBackgroundView(selected: false)
    private let selectedBackground = SettingsRowBackgroundView(selected: true)
    private var heightConstraint: NSLayoutConstraint?
    private weak var hostedContent: UIView?
    private var hostedConstraints: [NSLayoutConstraint] = []
    private var trailingSwitch: SettingsSwitch?
    private var trailingMenuButton: UIButton?
    private var trailingValueLabel: UILabel?
    private var customStack: UIStackView?
    private var customLeadingSlot: UIView?
    private var customTitleLabel: UILabel?
    private var customDetailLabel: UILabel?
    private var customTrailingSlot: UIView?

    public override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: .subtitle, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        backgroundView = normalBackground
        selectedBackgroundView = selectedBackground
        selectionStyle = .default
        let constraint = contentView.heightAnchor.constraint(greaterThanOrEqualToConstant: SettingsVisualMetrics.rowHeight)
        constraint.priority = UILayoutPriority(999)
        constraint.isActive = true
        heightConstraint = constraint
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func prepareForReuse() {
        super.prepareForReuse()
        detachHostedContent()
        detachCustomLayout()
        accessoryType = .none
        accessoryView = nil
        accessibilityIdentifier = nil
        accessibilityLabel = nil
        accessibilityValue = nil
        accessibilityTraits = .none
        isAccessibilityElement = false
        contentConfiguration = nil
    }

    private func detachHostedContent() {
        NSLayoutConstraint.deactivate(hostedConstraints)
        hostedConstraints.removeAll()
        hostedContent?.removeFromSuperview()
        hostedContent = nil
    }

    private func detachCustomLayout() {
        customLeadingSlot?.removeFromSuperview()
        customTrailingSlot?.removeFromSuperview()
        customStack?.removeFromSuperview()
        customStack = nil
        customLeadingSlot = nil
        customTitleLabel = nil
        customDetailLabel = nil
        customTrailingSlot = nil
        trailingSwitch = nil
        trailingMenuButton = nil
        trailingValueLabel = nil
    }

    public func configure(
        title: String,
        detail: String? = nil,
        detailDestructive: Bool = false,
        value: String? = nil,
        leadingView: UIView? = nil,
        trailingView: UIView? = nil,
        toggle: (isOn: Bool, enabled: Bool, onChange: @MainActor (Bool) -> Void)? = nil,
        menu: (title: String, menu: UIMenu, enabled: Bool)? = nil,
        embeddedContent: UIView? = nil,
        theme: PageTheme,
        first: Bool,
        last: Bool,
        destructive: Bool = false,
        enabled: Bool = true,
        actionable: Bool = false,
        selected: Bool = false,
        disclosure: Bool = false,
        usesSettingsColors: Bool = true,
        accessibilityIdentifier: String? = nil,
        accessibilityLabel: String? = nil,
        accessibilityValue: String? = nil
    ) {
        let showsSeparator = !last
        normalBackground.update(theme: theme, first: first, last: last, selected: false,
                                settings: usesSettingsColors, showsSeparator: showsSeparator)
        backgroundView = normalBackground
        if usesSettingsColors {
            selectedBackground.update(theme: theme, first: first, last: last, selected: true,
                                      settings: true, showsSeparator: showsSeparator)
            selectedBackgroundView = selectedBackground
        } else {
            selectedBackgroundView = nil
        }
        tintColor = UIColor(usesSettingsColors ? theme.settings.accent : theme.accent)
        heightConstraint?.constant = usesSettingsColors ? SettingsVisualMetrics.rowHeight : 56

        let margins = NSDirectionalEdgeInsets(
            top: SettingsVisualMetrics.rowVerticalPadding,
            leading: SettingsVisualMetrics.rowHorizontalPadding,
            bottom: SettingsVisualMetrics.rowVerticalPadding,
            trailing: SettingsVisualMetrics.rowHorizontalPadding
        )
        contentView.directionalLayoutMargins = margins

        if let embeddedContent {
            detachCustomLayout()
            contentConfiguration = nil
            if let themeable = embeddedContent as? any PageThemeable {
                themeable.applyTheme(theme)
            }
            if hostedContent !== embeddedContent || embeddedContent.superview !== contentView {
                detachHostedContent()
                embeddedContent.removeFromSuperview()
                contentView.addSubview(embeddedContent)
                embeddedContent.translatesAutoresizingMaskIntoConstraints = false
                let constraints = [
                    embeddedContent.topAnchor.constraint(equalTo: contentView.layoutMarginsGuide.topAnchor),
                    embeddedContent.bottomAnchor.constraint(equalTo: contentView.layoutMarginsGuide.bottomAnchor),
                    embeddedContent.leadingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.leadingAnchor),
                    embeddedContent.trailingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.trailingAnchor),
                ]
                NSLayoutConstraint.activate(constraints)
                hostedContent = embeddedContent
                hostedConstraints = constraints
            }
            selectionStyle = .none
            accessoryType = .none
            isAccessibilityElement = false
            self.accessibilityIdentifier = (accessibilityIdentifier?.isEmpty == false)
                ? accessibilityIdentifier : embeddedContent.accessibilityIdentifier
            return
        }

        detachHostedContent()

        let titleColor: UIColor
        let detailColor: UIColor
        if usesSettingsColors {
            titleColor = UIColor(!enabled ? theme.settings.muted :
                (destructive ? theme.settings.danger :
                    (actionable && !disclosure && toggle == nil && menu == nil && value == nil && trailingView == nil && leadingView == nil
                        ? theme.settings.accent : theme.settings.ink)))
            detailColor = UIColor(detailDestructive ? theme.settings.danger : theme.settings.muted)
        } else {
            titleColor = UIColor(enabled ? theme.ink : theme.muted)
            detailColor = UIColor(detailDestructive ? theme.settings.danger : theme.muted)
        }

        // Preserve live SettingsSwitch instance across diffable reconfigureItems so native
        // toggle animations are not torn down mid-flight.
        if let toggle, leadingView == nil, trailingView == nil, menu == nil, value == nil,
           let switchView = trailingSwitch, let titleLabel = customTitleLabel,
           customLeadingSlot == nil, customTrailingSlot == nil,
           (detail?.isEmpty == false) == (customDetailLabel != nil) {
            titleLabel.text = title
            titleLabel.textColor = titleColor
            if let detailLabel = customDetailLabel {
                detailLabel.text = detail
                detailLabel.textColor = detailColor
            }
            switchView.applyTheme(theme)
            if switchView.control.isOn != toggle.isOn {
                switchView.control.setOn(toggle.isOn, animated: window != nil)
            }
            switchView.control.isEnabled = enabled && toggle.enabled
            switchView.control.accessibilityLabel = accessibilityLabel ?? title
            switchView.control.accessibilityIdentifier = (accessibilityIdentifier?.isEmpty == false) ? accessibilityIdentifier : nil
            switchView.changed = toggle.onChange
            accessoryType = selected ? .checkmark : (disclosure ? .disclosureIndicator : .none)
            selectionStyle = .none
            self.accessibilityIdentifier = accessibilityIdentifier
            isAccessibilityElement = false
            return
        }

        detachCustomLayout()

        let needsCustomRow = leadingView != nil || trailingView != nil || toggle != nil || menu != nil || value != nil
        if needsCustomRow {
            contentConfiguration = nil
            let row = UIStackView()
            row.axis = .horizontal
            row.alignment = .center
            row.spacing = 12
            row.translatesAutoresizingMaskIntoConstraints = false

            if let leadingView {
                leadingView.removeFromSuperview()
                if let themeable = leadingView as? any PageThemeable { themeable.applyTheme(theme) }
                leadingView.setContentHuggingPriority(.required, for: .horizontal)
                leadingView.setContentCompressionResistancePriority(.required, for: .horizontal)
                row.addArrangedSubview(leadingView)
                customLeadingSlot = leadingView
            }

            let titleLabel = UILabel()
            titleLabel.text = title
            titleLabel.font = .preferredFont(forTextStyle: .body)
            titleLabel.adjustsFontForContentSizeCategory = true
            titleLabel.textColor = titleColor
            titleLabel.numberOfLines = 0
            titleLabel.setContentCompressionResistancePriority(.required, for: .vertical)
            customTitleLabel = titleLabel

            if let detail, !detail.isEmpty {
                let detailLabel = UILabel()
                detailLabel.text = detail
                detailLabel.font = .preferredFont(forTextStyle: .subheadline)
                detailLabel.adjustsFontForContentSizeCategory = true
                detailLabel.textColor = detailColor
                detailLabel.numberOfLines = 0
                detailLabel.setContentCompressionResistancePriority(.required, for: .vertical)
                customDetailLabel = detailLabel
                let textStack = UIStackView(arrangedSubviews: [titleLabel, detailLabel])
                textStack.axis = .vertical
                textStack.spacing = 2
                textStack.setContentHuggingPriority(.defaultLow, for: .horizontal)
                textStack.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
                row.addArrangedSubview(textStack)
            } else {
                titleLabel.setContentHuggingPriority(.defaultLow, for: .horizontal)
                titleLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
                row.addArrangedSubview(titleLabel)
            }

            if let value, !value.isEmpty {
                let valueLabel = UILabel()
                valueLabel.text = value
                valueLabel.font = .preferredFont(forTextStyle: .subheadline)
                valueLabel.adjustsFontForContentSizeCategory = true
                valueLabel.textColor = detailColor
                valueLabel.textAlignment = .right
                valueLabel.numberOfLines = 0
                valueLabel.setContentHuggingPriority(.required, for: .horizontal)
                valueLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
                row.addArrangedSubview(valueLabel)
                trailingValueLabel = valueLabel
            }

            if let toggle {
                let switchView = SettingsSwitch()
                switchView.applyTheme(theme)
                switchView.control.isOn = toggle.isOn
                switchView.control.isEnabled = enabled && toggle.enabled
                switchView.control.accessibilityLabel = accessibilityLabel ?? title
                if let id = accessibilityIdentifier, !id.isEmpty {
                    switchView.control.accessibilityIdentifier = id
                }
                switchView.changed = toggle.onChange
                row.addArrangedSubview(switchView)
                trailingSwitch = switchView
            } else if let menu {
                let button = UIButton(type: .system)
                var configuration = UIButton.Configuration.plain()
                configuration.title = menu.title
                configuration.image = UIImage(systemName: "chevron.up.chevron.down",
                    withConfiguration: UIImage.SymbolConfiguration(pointSize: 12, weight: .regular))
                configuration.imagePlacement = .trailing
                configuration.imagePadding = 5
                configuration.contentInsets = .zero
                configuration.baseForegroundColor = UIColor(enabled && menu.enabled ? theme.settings.accent : theme.settings.muted)
                configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { incoming in
                    var attributes = incoming
                    attributes.font = .preferredFont(forTextStyle: .subheadline)
                    return attributes
                }
                button.configuration = configuration
                button.menu = menu.menu
                button.showsMenuAsPrimaryAction = true
                button.isEnabled = enabled && menu.enabled
                button.setContentHuggingPriority(.required, for: .horizontal)
                button.setContentCompressionResistancePriority(.required, for: .horizontal)
                button.accessibilityLabel = accessibilityLabel ?? title
                button.accessibilityValue = menu.title
                if let id = accessibilityIdentifier, !id.isEmpty {
                    button.accessibilityIdentifier = id
                }
                row.addArrangedSubview(button)
                trailingMenuButton = button
            } else if let trailingView {
                trailingView.removeFromSuperview()
                if let themeable = trailingView as? any PageThemeable { themeable.applyTheme(theme) }
                trailingView.setContentHuggingPriority(.required, for: .horizontal)
                trailingView.setContentCompressionResistancePriority(.required, for: .horizontal)
                row.addArrangedSubview(trailingView)
                customTrailingSlot = trailingView
            }

            contentView.addSubview(row)
            NSLayoutConstraint.activate([
                row.topAnchor.constraint(equalTo: contentView.layoutMarginsGuide.topAnchor),
                row.bottomAnchor.constraint(equalTo: contentView.layoutMarginsGuide.bottomAnchor),
                row.leadingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.leadingAnchor),
                row.trailingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.trailingAnchor),
            ])
            customStack = row
            accessoryType = selected ? .checkmark : (disclosure ? .disclosureIndicator : .none)
            selectionStyle = (!actionable || !enabled || toggle != nil || menu != nil) ? .none : .default
            self.accessibilityIdentifier = accessibilityIdentifier
            if toggle != nil || menu != nil {
                isAccessibilityElement = false
            } else {
                isAccessibilityElement = true
                self.accessibilityLabel = accessibilityLabel ?? title
                self.accessibilityValue = accessibilityValue ?? value ?? detail
                accessibilityTraits = actionable ? .button : .staticText
                if !enabled { accessibilityTraits.insert(.notEnabled) }
                if selected { accessibilityTraits.insert(.selected) }
            }
            return
        }

        var content = UIListContentConfiguration.subtitleCell()
        content.text = title
        content.secondaryText = detail
        content.directionalLayoutMargins = margins
        content.textProperties.font = .preferredFont(forTextStyle: .body)
        content.textProperties.adjustsFontForContentSizeCategory = true
        content.textProperties.color = titleColor
        content.textProperties.numberOfLines = 0
        content.secondaryTextProperties.font = .preferredFont(forTextStyle: .subheadline)
        content.secondaryTextProperties.adjustsFontForContentSizeCategory = true
        content.secondaryTextProperties.color = detailColor
        content.secondaryTextProperties.numberOfLines = 0
        contentConfiguration = content

        accessoryType = selected ? .checkmark : (disclosure ? .disclosureIndicator : .none)
        selectionStyle = (!actionable || !enabled) ? .none : .default
        isAccessibilityElement = true
        self.accessibilityIdentifier = accessibilityIdentifier
        self.accessibilityLabel = accessibilityLabel ?? title
        self.accessibilityValue = accessibilityValue ?? detail
        accessibilityTraits = actionable ? .button : .staticText
        if !enabled { accessibilityTraits.insert(.notEnabled) }
        if selected { accessibilityTraits.insert(.selected) }
    }
}
#endif
