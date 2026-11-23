#if os(iOS)
import UIKit
import MeowDebug

/// A reusable settings row built once with persistent slots (zero view-hierarchy
/// teardown or reallocation on diffable `reconfigureItems`).
@MainActor
public final class SettingsTableCell: UITableViewCell {
    public static let reuseIdentifier = "meow.settings.cell"

    private let normalBackground = SettingsRowBackgroundView(selected: false)
    private let selectedBackground = SettingsRowBackgroundView(selected: true)
    private var heightConstraint: NSLayoutConstraint?

    // Persistent standard row slot hierarchy (allocated once in init, updated in place).
    private let rowStack = UIStackView()
    private let textStack = UIStackView()
    private let titleLabel = UILabel()
    private let detailLabel = UILabel()
    private let valueLabel = UILabel()
    private let switchView = SettingsSwitch()
    private let menuButton = UIButton(type: .system)
    private let actionButton = UIButton(type: .system)
    private let loadingSpinner = UIActivityIndicatorView(style: .medium)
    private let hintLabel = UILabel()

    private weak var leadingSlot: ThemeableView?
    private weak var customTrailingSlot: ThemeableView?
    private weak var hostedContent: ThemeableView?
    private var rowStackConstraints: [NSLayoutConstraint] = []
    private var hostedConstraints: [NSLayoutConstraint] = []
    private var onActionTap: (@MainActor () -> Void)?

    public override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        DebugUIMetrics.shared.increment("settings.cell.created")
        backgroundColor = .clear
        backgroundView = normalBackground
        selectedBackgroundView = selectedBackground
        selectionStyle = .default

        let constraint = contentView.heightAnchor.constraint(greaterThanOrEqualToConstant: SettingsVisualMetrics.rowHeight)
        constraint.priority = UILayoutPriority(999)
        constraint.isActive = true
        heightConstraint = constraint

        titleLabel.font = .preferredFont(forTextStyle: .body)
        titleLabel.adjustsFontForContentSizeCategory = true
        titleLabel.numberOfLines = 0
        titleLabel.setContentCompressionResistancePriority(.required, for: .vertical)

        detailLabel.font = .preferredFont(forTextStyle: .subheadline)
        detailLabel.adjustsFontForContentSizeCategory = true
        detailLabel.numberOfLines = 0
        detailLabel.setContentCompressionResistancePriority(.required, for: .vertical)
        detailLabel.isHidden = true

        textStack.axis = .vertical
        textStack.spacing = 2
        textStack.setContentHuggingPriority(.defaultLow, for: .horizontal)
        textStack.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        textStack.addArrangedSubview(titleLabel)
        textStack.addArrangedSubview(detailLabel)

        valueLabel.font = .preferredFont(forTextStyle: .subheadline)
        valueLabel.adjustsFontForContentSizeCategory = true
        valueLabel.textAlignment = .right
        valueLabel.numberOfLines = 0
        valueLabel.setContentHuggingPriority(.required, for: .horizontal)
        valueLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
        valueLabel.isHidden = true

        switchView.setContentHuggingPriority(.required, for: .horizontal)
        switchView.setContentCompressionResistancePriority(.required, for: .horizontal)
        switchView.isHidden = true

        menuButton.showsMenuAsPrimaryAction = true
        menuButton.setContentHuggingPriority(.required, for: .horizontal)
        menuButton.setContentCompressionResistancePriority(.required, for: .horizontal)
        menuButton.isHidden = true

        actionButton.titleLabel?.font = .preferredFont(forTextStyle: .subheadline)
        actionButton.titleLabel?.adjustsFontForContentSizeCategory = true
        actionButton.setContentHuggingPriority(.required, for: .horizontal)
        actionButton.setContentCompressionResistancePriority(.required, for: .horizontal)
        actionButton.addAction(UIAction { [weak self] _ in self?.onActionTap?() }, for: .touchUpInside)
        actionButton.isHidden = true

        loadingSpinner.hidesWhenStopped = true
        loadingSpinner.setContentHuggingPriority(.required, for: .horizontal)
        loadingSpinner.setContentCompressionResistancePriority(.required, for: .horizontal)
        loadingSpinner.isHidden = true

        hintLabel.font = .preferredFont(forTextStyle: .body)
        hintLabel.adjustsFontForContentSizeCategory = true
        hintLabel.textAlignment = .right
        hintLabel.widthAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        hintLabel.setContentHuggingPriority(.required, for: .horizontal)
        hintLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
        hintLabel.isHidden = true

        rowStack.axis = .horizontal
        rowStack.alignment = .center
        rowStack.spacing = 12
        rowStack.translatesAutoresizingMaskIntoConstraints = false
        for view in [textStack, valueLabel, switchView, menuButton, actionButton, loadingSpinner, hintLabel] as [UIView] {
            rowStack.addArrangedSubview(view)
        }

        contentView.addSubview(rowStack)
        rowStackConstraints = [
            rowStack.topAnchor.constraint(equalTo: contentView.layoutMarginsGuide.topAnchor),
            rowStack.bottomAnchor.constraint(equalTo: contentView.layoutMarginsGuide.bottomAnchor),
            rowStack.leadingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.leadingAnchor),
            rowStack.trailingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.trailingAnchor),
        ]
        NSLayoutConstraint.activate(rowStackConstraints)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func prepareForReuse() {
        super.prepareForReuse()
        detachHostedContent()
        detachLeadingSlot()
        detachCustomTrailingSlot()
        switchView.changed = nil
        switchView.isHidden = true
        menuButton.menu = nil
        menuButton.isHidden = true
        onActionTap = nil
        actionButton.isHidden = true
        loadingSpinner.stopAnimating()
        loadingSpinner.isHidden = true
        hintLabel.text = nil
        hintLabel.isHidden = true
        valueLabel.text = nil
        valueLabel.isHidden = true
        titleLabel.text = nil
        detailLabel.text = nil
        detailLabel.isHidden = true
        rowStack.isHidden = false
        NSLayoutConstraint.activate(rowStackConstraints)
        accessoryType = .none
        accessoryView = nil
        accessibilityIdentifier = nil
        accessibilityLabel = nil
        accessibilityValue = nil
        accessibilityTraits = .none
        isAccessibilityElement = false
    }

    private func detachHostedContent() {
        NSLayoutConstraint.deactivate(hostedConstraints)
        hostedConstraints.removeAll()
        hostedContent?.removeFromSuperview()
        hostedContent = nil
    }

    private func detachLeadingSlot() {
        guard let existing = leadingSlot else { return }
        rowStack.removeArrangedSubview(existing)
        existing.removeFromSuperview()
        leadingSlot = nil
    }

    private func detachCustomTrailingSlot() {
        guard let existing = customTrailingSlot else { return }
        rowStack.removeArrangedSubview(existing)
        existing.removeFromSuperview()
        customTrailingSlot = nil
    }

    public func refreshInteractiveHandlers(row: SettingsListRow) {
        switch row.accessory {
        case .toggle(let isOn, _, let onChange):
            switchView.changed = onChange
            if switchView.control.isOn != isOn {
                switchView.control.setOn(isOn, animated: true)
            }
        case .menu(_, let menu, _):
            menuButton.menu = menu
        case .action(_, _, _, let onTap):
            onActionTap = onTap
        default:
            break
        }
    }

    public func configure(
        row: SettingsListRow,
        theme: PageTheme,
        first: Bool,
        last: Bool,
        usesSettingsColors: Bool = true
    ) {
        DebugUIMetrics.shared.increment("settings.cell.configured")
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

        if let embedded = row.embeddedContent {
            NSLayoutConstraint.deactivate(rowStackConstraints)
            rowStack.isHidden = true
            titleLabel.text = nil
            detailLabel.text = nil
            detailLabel.isHidden = true
            detachLeadingSlot()
            detachCustomTrailingSlot()
            switchView.changed = nil
            onActionTap = nil
            menuButton.menu = nil
            loadingSpinner.stopAnimating()
            embedded.applyTheme(theme)
            if hostedContent !== embedded || embedded.superview !== contentView {
                detachHostedContent()
                embedded.removeFromSuperview()
                contentView.addSubview(embedded)
                embedded.translatesAutoresizingMaskIntoConstraints = false
                let constraints = [
                    embedded.topAnchor.constraint(equalTo: contentView.layoutMarginsGuide.topAnchor),
                    embedded.bottomAnchor.constraint(equalTo: contentView.layoutMarginsGuide.bottomAnchor),
                    embedded.leadingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.leadingAnchor),
                    embedded.trailingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.trailingAnchor),
                ]
                NSLayoutConstraint.activate(constraints)
                hostedContent = embedded
                hostedConstraints = constraints
            }
            selectionStyle = .none
            accessoryType = .none
            isAccessibilityElement = false
            accessibilityIdentifier = row.id.isEmpty ? embedded.accessibilityIdentifier : row.id
            return
        }

        detachHostedContent()
        rowStack.isHidden = false
        NSLayoutConstraint.activate(rowStackConstraints)

        var value: String?
        var disclosure = false
        var selected = false
        var toggleSpec: (isOn: Bool, enabled: Bool, onChange: @MainActor (Bool) -> Void)?
        var menuSpec: (title: String, menu: UIMenu, enabled: Bool)?
        var actionSpec: (title: String, enabled: Bool, destructive: Bool, onTap: @MainActor () -> Void)?
        var loadingLabel: String?
        var hintText: String?
        var customTrailing: ThemeableView?

        switch row.accessory {
        case .none:
            break
        case .disclosure:
            disclosure = true
        case .checkmark:
            selected = true
        case .value(let text, let showsDisclosure):
            value = text
            disclosure = showsDisclosure
        case .toggle(let isOn, let toggleEnabled, let onChange):
            toggleSpec = (isOn, toggleEnabled, onChange)
        case .menu(let title, let uiMenu, let menuEnabled):
            menuSpec = (title, uiMenu, menuEnabled)
        case .loading(let label):
            loadingLabel = label
        case .hint(let text):
            hintText = text
        case .action(let title, let actionEnabled, let destructive, let onTap):
            actionSpec = (title, actionEnabled, destructive, onTap)
        case .custom(let view):
            customTrailing = view
        }

        let hasTrailing = value != nil || toggleSpec != nil || menuSpec != nil
            || actionSpec != nil || loadingLabel != nil || hintText != nil || customTrailing != nil
        let isActionTitle = row.action != nil && !disclosure && !hasTrailing && row.leadingView == nil

        let titleColor: UIColor
        let detailColor: UIColor
        if usesSettingsColors {
            titleColor = UIColor(!row.enabled ? theme.settings.muted :
                (row.destructive ? theme.settings.danger :
                    (isActionTitle ? theme.settings.accent : theme.settings.ink)))
            detailColor = UIColor(row.detailDestructive ? theme.settings.danger : theme.settings.muted)
        } else {
            titleColor = UIColor(row.enabled ? theme.ink : theme.muted)
            detailColor = UIColor(row.detailDestructive ? theme.settings.danger : theme.muted)
        }

        // Leading slot (ThemeableView enforced at compile time).
        if let leading = row.leadingView {
            leading.applyTheme(theme)
            if leadingSlot !== leading || leading.superview !== rowStack {
                detachLeadingSlot()
                leading.removeFromSuperview()
                leading.setContentHuggingPriority(.required, for: .horizontal)
                leading.setContentCompressionResistancePriority(.required, for: .horizontal)
                rowStack.insertArrangedSubview(leading, at: 0)
                leadingSlot = leading
            }
        } else {
            detachLeadingSlot()
        }

        titleLabel.text = row.title
        titleLabel.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
        titleLabel.textColor = titleColor

        if let detail = row.detail, !detail.isEmpty {
            detailLabel.text = detail
            detailLabel.font = .preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection)
            detailLabel.textColor = detailColor
            detailLabel.isHidden = false
        } else {
            detailLabel.text = nil
            detailLabel.isHidden = true
        }

        if let value, !value.isEmpty {
            valueLabel.text = value
            valueLabel.font = .preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection)
            valueLabel.textColor = detailColor
            valueLabel.isHidden = false
        } else {
            valueLabel.text = nil
            valueLabel.isHidden = true
        }

        if let toggleSpec {
            switchView.isHidden = false
            switchView.applyTheme(theme)
            if switchView.control.isOn != toggleSpec.isOn {
                switchView.control.setOn(toggleSpec.isOn, animated: window != nil)
            }
            switchView.control.isEnabled = row.enabled && toggleSpec.enabled
            switchView.control.accessibilityLabel = row.accessibilityLabel ?? row.title
            switchView.control.accessibilityIdentifier = row.id.isEmpty ? nil : row.id
            switchView.changed = toggleSpec.onChange
        } else {
            switchView.changed = nil
            switchView.isHidden = true
        }

        let accentColor = usesSettingsColors ? theme.settings.accent : theme.accent
        let mutedColor = usesSettingsColors ? theme.settings.muted : theme.muted
        let dangerColor = theme.settings.danger

        if let menuSpec {
            menuButton.isHidden = false
            var configuration = UIButton.Configuration.plain()
            configuration.title = menuSpec.title
            configuration.image = UIImage(
                systemName: "chevron.up.chevron.down",
                withConfiguration: UIImage.SymbolConfiguration(pointSize: 12, weight: .regular)
            )
            configuration.imagePlacement = .trailing
            configuration.imagePadding = 5
            configuration.contentInsets = .zero
            configuration.baseForegroundColor = UIColor(row.enabled && menuSpec.enabled ? accentColor : mutedColor)
            configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { [weak self] incoming in
                var attributes = incoming
                attributes.font = .preferredFont(forTextStyle: .subheadline, compatibleWith: self?.traitCollection)
                return attributes
            }
            menuButton.configuration = configuration
            menuButton.menu = menuSpec.menu
            menuButton.isEnabled = row.enabled && menuSpec.enabled
            menuButton.accessibilityLabel = row.accessibilityLabel ?? row.title
            menuButton.accessibilityValue = menuSpec.title
            menuButton.accessibilityIdentifier = row.id.isEmpty ? nil : row.id
        } else {
            menuButton.menu = nil
            menuButton.isHidden = true
        }

        if let actionSpec {
            actionButton.isHidden = false
            actionButton.setTitle(actionSpec.title, for: .normal)
            actionButton.titleLabel?.font = .preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection)
            let isInteractive = row.enabled && actionSpec.enabled
            let color = isInteractive ? (actionSpec.destructive ? dangerColor : accentColor) : mutedColor
            actionButton.setTitleColor(UIColor(color), for: .normal)
            actionButton.isEnabled = isInteractive
            actionButton.accessibilityLabel = actionSpec.title
            onActionTap = actionSpec.onTap
        } else {
            onActionTap = nil
            actionButton.isHidden = true
        }

        if let loadingLabel {
            loadingSpinner.color = UIColor(mutedColor)
            loadingSpinner.accessibilityLabel = loadingLabel
            loadingSpinner.isHidden = false
            if !loadingSpinner.isAnimating { loadingSpinner.startAnimating() }
        } else {
            loadingSpinner.stopAnimating()
            loadingSpinner.isHidden = true
        }

        if let hintText {
            hintLabel.text = hintText
            hintLabel.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
            hintLabel.textColor = UIColor(accentColor)
            hintLabel.isHidden = false
        } else {
            hintLabel.text = nil
            hintLabel.isHidden = true
        }

        if let customTrailing {
            customTrailing.applyTheme(theme)
            if customTrailingSlot !== customTrailing || customTrailing.superview !== rowStack {
                detachCustomTrailingSlot()
                customTrailing.removeFromSuperview()
                customTrailing.setContentHuggingPriority(.required, for: .horizontal)
                customTrailing.setContentCompressionResistancePriority(.required, for: .horizontal)
                rowStack.addArrangedSubview(customTrailing)
                customTrailingSlot = customTrailing
            }
        } else {
            detachCustomTrailingSlot()
        }

        accessoryType = selected ? .checkmark : (disclosure ? .disclosureIndicator : .none)
        let hasInlineControl = toggleSpec != nil || menuSpec != nil || actionSpec != nil
        selectionStyle = (row.action == nil || !row.enabled || hasInlineControl) ? .none : .default
        accessibilityIdentifier = row.id.isEmpty ? nil : row.id
        if hasInlineControl {
            isAccessibilityElement = false
        } else {
            isAccessibilityElement = true
            accessibilityLabel = row.accessibilityLabel ?? row.title
            accessibilityValue = row.accessibilityValue ?? value ?? row.detail
            accessibilityTraits = row.action != nil ? .button : .staticText
            if !row.enabled { accessibilityTraits.insert(.notEnabled) }
            if selected { accessibilityTraits.insert(.selected) }
        }
    }
}
#endif
