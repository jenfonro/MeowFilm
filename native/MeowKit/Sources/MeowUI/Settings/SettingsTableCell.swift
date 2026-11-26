#if os(iOS)
import UIKit
import MeowDebug

/// A reusable settings row built once with persistent slots (zero view-hierarchy
/// teardown or reallocation on diffable `reconfigureItems`).
@MainActor
public final class SettingsTableCell: UITableViewCell {
    public static let reuseIdentifier = "meow.settings.cell"

    private let cardBackground = SettingsRowBackgroundView(selected: false)
    private var heightConstraint: NSLayoutConstraint?
    private var currentTheme = PageTheme()
    private var isFirstRow = true
    private var isLastRow = true
    private var currentUsesSettingsColors = true

    // Persistent standard row slot hierarchy (allocated once in init, updated in place).
    private let rowStack = UIStackView()
    private let leadingIconContainer = UIView()
    private let leadingIconView = UIImageView()
    private var leadingIconWidth: NSLayoutConstraint!
    private var leadingIconHeight: NSLayoutConstraint!
    private let textStack = UIStackView()
    private let titleLabel = UILabel()
    private let detailLabel = UILabel()
    private let valueLabel = UILabel()
    private let switchView = SettingsSwitch()
    private let menuButton = UIButton(type: .system)
    private let actionButton = UIButton(type: .system)
    private let loadingSpinner = UIActivityIndicatorView(style: .medium)
    private let hintLabel = UILabel()

    private weak var hostedContent: ThemeableView?
    private var rowStackConstraints: [NSLayoutConstraint] = []
    private var hostedConstraints: [NSLayoutConstraint] = []
    private var onActionTap: (@MainActor () -> Void)?

    public override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        DebugUIMetrics.shared.increment("settings.cell.created")
        backgroundColor = .clear
        backgroundView = cardBackground
        selectedBackgroundView = UIView()
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

        leadingIconContainer.isAccessibilityElement = false
        leadingIconContainer.clipsToBounds = true
        leadingIconContainer.layer.cornerCurve = .continuous
        leadingIconContainer.setContentHuggingPriority(.required, for: .horizontal)
        leadingIconContainer.setContentCompressionResistancePriority(.required, for: .horizontal)
        leadingIconContainer.translatesAutoresizingMaskIntoConstraints = false
        leadingIconWidth = leadingIconContainer.widthAnchor.constraint(equalToConstant: 24)
        leadingIconHeight = leadingIconContainer.heightAnchor.constraint(equalToConstant: 24)
        NSLayoutConstraint.activate([leadingIconWidth, leadingIconHeight])
        leadingIconView.isAccessibilityElement = false
        leadingIconView.translatesAutoresizingMaskIntoConstraints = false
        leadingIconContainer.addSubview(leadingIconView)
        leadingIconView.pinEdges(to: leadingIconContainer)
        leadingIconContainer.isHidden = true

        rowStack.axis = .horizontal
        rowStack.alignment = .center
        rowStack.spacing = 12
        rowStack.translatesAutoresizingMaskIntoConstraints = false
        for view in [leadingIconContainer, textStack, valueLabel, switchView, menuButton, actionButton, loadingSpinner, hintLabel] as [UIView] {
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
        leadingIconView.image = nil
        leadingIconContainer.isHidden = true
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
        showsReorderControl = false
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

    public func refreshInteractiveHandlers(row: SettingsListRow) {
        switch row.accessory {
        case .toggle(let isOn, _, let onChange):
            switchView.changed = onChange
            if switchView.isOn != isOn {
                switchView.setOn(isOn, animated: true)
            }
        case .menu(_, let menu, _):
            menuButton.menu = menu
        case .action(_, _, _, let onTap):
            onActionTap = onTap
        default:
            break
        }
    }

    public override func updateConfiguration(using state: UICellConfigurationState) {
        super.updateConfiguration(using: state)
        let active = currentUsesSettingsColors && selectionStyle != .none && (state.isHighlighted || state.isSelected)
        cardBackground.update(theme: currentTheme, first: isFirstRow, last: isLastRow,
                              selected: active, settings: currentUsesSettingsColors)
    }

    public func configure(
        row: SettingsListRow,
        theme: PageTheme,
        first: Bool,
        last: Bool,
        usesSettingsColors: Bool = true
    ) {
        DebugUIMetrics.shared.increment("settings.cell.configured")
        currentTheme = theme
        isFirstRow = first
        isLastRow = last
        currentUsesSettingsColors = usesSettingsColors
        cardBackground.update(theme: theme, first: first, last: last,
                              selected: usesSettingsColors && selectionStyle != .none && (isHighlighted || isSelected),
                              settings: usesSettingsColors)
        backgroundView = cardBackground
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
            leadingIconView.image = nil
            leadingIconContainer.isHidden = true
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
        }

        let hasTrailing = value != nil || toggleSpec != nil || menuSpec != nil
            || actionSpec != nil || loadingLabel != nil || hintText != nil
        let isActionTitle = row.action != nil && !disclosure && !hasTrailing && row.leadingIcon == nil

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

        // Persistent leading icon slot (zero view attach/detach).
        if let icon = row.leadingIcon {
            configureLeadingIcon(icon, theme: theme)
        } else {
            leadingIconView.image = nil
            leadingIconContainer.isHidden = true
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
            if switchView.isOn != toggleSpec.isOn {
                switchView.setOn(toggleSpec.isOn, animated: window != nil)
            }
            switchView.isEnabled = row.enabled && toggleSpec.enabled
            switchView.accessibilityLabel = row.accessibilityLabel ?? row.title
            switchView.accessibilityIdentifier = row.id.isEmpty ? nil : row.id
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

        accessoryType = selected ? .checkmark : (disclosure ? .disclosureIndicator : .none)
        showsReorderControl = row.canMove
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

    private func configureLeadingIcon(_ icon: SettingsListRow.LeadingIcon, theme: PageTheme) {
        leadingIconContainer.isHidden = false
        switch icon {
        case .symbol(let name):
            leadingIconWidth.constant = 24
            leadingIconHeight.constant = 24
            leadingIconContainer.layer.cornerRadius = 0
            leadingIconContainer.backgroundColor = .clear
            leadingIconView.image = UIImage(
                systemName: name,
                withConfiguration: UIImage.SymbolConfiguration(pointSize: 17, weight: .regular)
            )
            leadingIconView.contentMode = .center
            leadingIconView.tintColor = UIColor(theme.settings.muted)
        case .asset(let assetName, let fallbackSymbol):
            leadingIconWidth.constant = 22
            leadingIconHeight.constant = 22
            leadingIconContainer.layer.cornerRadius = 0
            leadingIconContainer.backgroundColor = .clear
            if let assetName, let asset = UIImage(named: assetName) {
                leadingIconView.image = asset
                leadingIconView.contentMode = .scaleAspectFit
            } else {
                leadingIconView.image = UIImage(
                    systemName: fallbackSymbol,
                    withConfiguration: UIImage.SymbolConfiguration(pointSize: 15, weight: .regular)
                )
                leadingIconView.contentMode = .center
                leadingIconView.tintColor = UIColor(theme.settings.muted)
            }
        case .badge(let assetName, let fallbackSymbol, let size):
            leadingIconWidth.constant = size
            leadingIconHeight.constant = size
            leadingIconContainer.layer.cornerRadius = size * 0.28
            if let assetName, let asset = UIImage(named: assetName) {
                leadingIconContainer.backgroundColor = .clear
                leadingIconView.image = asset
                leadingIconView.contentMode = .scaleAspectFill
            } else {
                leadingIconContainer.backgroundColor = UIColor(theme.settings.input)
                leadingIconView.image = UIImage(
                    systemName: fallbackSymbol,
                    withConfiguration: UIImage.SymbolConfiguration(pointSize: size * 0.45, weight: .regular)
                )
                leadingIconView.contentMode = .center
                leadingIconView.tintColor = UIColor(theme.settings.accent)
            }
        }
    }
}
#endif
