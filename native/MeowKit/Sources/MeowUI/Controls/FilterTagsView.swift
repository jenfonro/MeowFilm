// SPDX-License-Identifier: GPL-3.0-only
#if canImport(UIKit)
import UIKit

@MainActor
open class FilterTagsView: UIView {
    private enum Geometry {
        static let inset: CGFloat = 16
        static let chipHeight: CGFloat = 30
        static let chipSpacing: CGFloat = 8
        static let rowTop: CGFloat = 4
        static let rowSpacing: CGFloat = 12
        static let toggleSize: CGFloat = 44
        static let toggleIconSize: CGFloat = 18
        static let toggleGap: CGFloat = 8
        static let collapsedHeight: CGFloat = 56
        static let maximumExpandedHeight: CGFloat = 180
        static let expandedBottomInset: CGFloat = 18
        static let expandedCornerRadius: CGFloat = 16
        static var toggleIconInset: CGFloat { (toggleSize - toggleIconSize) / 2 }
        static var toggleLeadingExtent: CGFloat { toggleSize - toggleIconInset }
        static var expandedVerticalPadding: CGFloat { rowTop + expandedBottomInset }
    }

    private struct LayoutMetrics {
        let sizes: [CGSize]
        let chipWidth: CGFloat
        let overflow: Bool
    }

    public var onExpansionChanged: (() -> Void)?
    public private(set) var isExpanded = false
    public private(set) var hasOverflow = false
    private let scroll = UIScrollView()
    private let toggle = UIButton(type: .system)
    private let expandedBackground: WindowGradientView
    private let identifier: String
    private var filters: [FilterTag] = []
    private var buttons: [String: FilterTagButton] = [:]
    private var selectedID = ""
    private var onSelect: ((String) -> Void)?
    private var layoutWidth: CGFloat?
    private var revealSelection = false
    public var collapsedHeight: CGFloat { filters.count > 1 ? Geometry.collapsedHeight : 0 }

    public init(themeProvider: any PageThemeProviding, identifier: String) {
        self.identifier = identifier
        expandedBackground = WindowGradientView(themeProvider: themeProvider)
        super.init(frame: .zero)
        scroll.showsHorizontalScrollIndicator = false
        scroll.showsVerticalScrollIndicator = false
        scroll.alwaysBounceHorizontal = false
        scroll.isDirectionalLockEnabled = true
        scroll.contentInsetAdjustmentBehavior = .never
        layer.maskedCorners = [.layerMinXMaxYCorner, .layerMaxXMaxYCorner]
        toggle.setImage(PageIcon.image(.down, size: Geometry.toggleIconSize), for: .normal)
        toggle.accessibilityIdentifier = "\(identifier).tags.toggle"
        toggle.isHidden = true
        toggle.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.updateLayout(width: self.bounds.width)
            guard self.hasOverflow else { return }
            self.setExpanded(!self.isExpanded, notify: true)
        }, for: .touchUpInside)
        expandedBackground.isHidden = true
        expandedBackground.accessibilityElementsHidden = true
        addSubview(expandedBackground)
        addSubview(scroll)
        addSubview(toggle)
        expandedBackground.pinEdges(to: self)
        accessibilityIdentifier = "\(identifier).tags"
        updateToggle()
        applyExpandedAppearance()
    }

    @available(*, unavailable)
    public required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public func collapse() {
        setExpanded(false, notify: true)
    }

    public func configure(filters: [FilterTag], selectedID: String, theme: PageTheme,
                   onSelect: @escaping (String) -> Void) {
        let rebuild = filters != self.filters
        let selectionChanged = rebuild || selectedID != self.selectedID
        self.filters = filters
        self.selectedID = selectedID
        self.onSelect = onSelect
        if rebuild {
            scroll.setContentOffset(.zero, animated: false)
            for button in buttons.values { button.removeFromSuperview() }
            buttons = [:]
            for filter in filters {
                let button = FilterTagButton(filter: filter, identifier: identifier)
                button.addAction(UIAction { [weak self] _ in
                    guard let self else { return }
                    self.collapse()
                    self.onSelect?(filter.id)
                }, for: .touchUpInside)
                buttons[filter.id] = button
                scroll.addSubview(button)
            }
        }
        for filter in filters {
            buttons[filter.id]?.configure(selected: filter.id == selectedID, theme: theme)
        }
        toggle.tintColor = UIColor(theme.muted)
        updateLayout(width: bounds.width)
        applyExpandedAppearance()
        setNeedsLayout()
        if selectionChanged { revealSelection = true }
    }

    /// Call after the available width changes. This owns overflow and
    /// expansion state; height queries below remain read-only.
    public func updateLayout(width: CGFloat) {
        let widthChanged = layoutWidth != width
        layoutWidth = width
        let available = filters.count > 1 && width > 0
        let metrics = available ? layoutMetrics(width: width) : nil
        let overflow = metrics?.overflow ?? false
        let overflowChanged = hasOverflow != overflow
        hasOverflow = overflow
        if !available || !overflow {
            scroll.setContentOffset(.zero, animated: false)
            setExpanded(false)
        }
        toggle.isHidden = !overflow
        if widthChanged || overflowChanged { setNeedsLayout() }
    }

    public func preferredHeight(for width: CGFloat) -> CGFloat {
        guard filters.count > 1, width > 0 else { return 0 }
        let metrics = layoutMetrics(width: width)
        guard isExpanded, metrics.overflow else { return collapsedHeight }
        let rows = FilterTagRows(
            width: metrics.chipWidth, items: metrics.sizes, rowSpacing: Geometry.rowSpacing
        )
        return min(Geometry.maximumExpandedHeight, rows.size.height + Geometry.expandedVerticalPadding)
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        guard filters.count > 1, bounds.width > 0 else {
            scroll.frame = .zero
            toggle.frame = .zero
            scroll.isScrollEnabled = false
            return
        }
        let metrics = layoutMetrics(width: bounds.width)
        if isExpanded {
            let rows = FilterTagRows(
                width: metrics.chipWidth, items: metrics.sizes, rowSpacing: Geometry.rowSpacing
            )
            let visibleHeight = max(0, bounds.height - Geometry.expandedVerticalPadding)
            scroll.frame = CGRect(x: Geometry.inset, y: Geometry.rowTop,
                                  width: metrics.chipWidth, height: visibleHeight)
            scroll.contentSize = rows.size
            for (filter, frame) in zip(filters, rows.frames) { buttons[filter.id]?.frame = frame }
            scroll.isScrollEnabled = rows.size.height > visibleHeight + 0.5
        } else {
            scroll.frame = CGRect(x: Geometry.inset, y: Geometry.rowTop,
                                  width: metrics.chipWidth, height: Geometry.chipHeight)
            var x: CGFloat = 0
            for (filter, size) in zip(filters, metrics.sizes) {
                buttons[filter.id]?.frame = CGRect(x: x, y: 0, width: size.width, height: size.height)
                x += size.width + Geometry.chipSpacing
            }
            scroll.contentSize = CGSize(width: max(0, x - Geometry.chipSpacing), height: Geometry.chipHeight)
            scroll.isScrollEnabled = metrics.overflow
            if revealSelection, let selected = buttons[selectedID] {
                revealSelection = false
                scroll.scrollRectToVisible(selected.frame, animated: false)
            }
        }
        toggle.frame = metrics.overflow ? toggleFrame(width: bounds.width) : .zero
    }

    private func layoutMetrics(width: CGFloat) -> LayoutMetrics {
        let font = UIFont.systemFont(ofSize: 13)
        let sizes = filters.map {
            CGSize(width: ceil(($0.title as NSString).size(withAttributes: [.font: font]).width) + 26,
                   height: Geometry.chipHeight)
        }
        let intrinsic = sizes.reduce(CGFloat.zero) { $0 + $1.width } +
            CGFloat(max(0, sizes.count - 1)) * Geometry.chipSpacing
        let fullWidth = max(1, width - Geometry.inset * 2)
        let overflow = intrinsic > fullWidth + 1
        let chipWidth = overflow
            ? max(1, fullWidth - Geometry.toggleLeadingExtent - Geometry.toggleGap)
            : fullWidth
        return LayoutMetrics(sizes: sizes, chipWidth: chipWidth, overflow: overflow)
    }

    private func toggleFrame(width: CGFloat) -> CGRect {
        let maxX = width - Geometry.inset + Geometry.toggleIconInset
        let y = Geometry.rowTop + Geometry.chipHeight / 2 - Geometry.toggleSize / 2
        return CGRect(x: maxX - Geometry.toggleSize, y: y,
                      width: Geometry.toggleSize, height: Geometry.toggleSize)
    }

    private func updateToggle() {
        toggle.accessibilityLabel = isExpanded ? "收起标签" : "展开标签"
        toggle.accessibilityValue = isExpanded ? "已展开" : "已收起"
        toggle.transform = isExpanded ? CGAffineTransform(rotationAngle: .pi) : .identity
    }

    private func setExpanded(_ expanded: Bool, notify: Bool = false) {
        guard isExpanded != expanded else { return }
        isExpanded = expanded
        scroll.setContentOffset(.zero, animated: false)
        updateToggle()
        applyExpandedAppearance()
        setNeedsLayout()
        if !expanded { revealSelection = true }
        if notify { onExpansionChanged?() }
    }

    private func applyExpandedAppearance() {
        expandedBackground.isHidden = !isExpanded
        backgroundColor = .clear
        layer.cornerRadius = isExpanded ? Geometry.expandedCornerRadius : 0
        clipsToBounds = isExpanded
    }
}

@MainActor
private final class FilterTagButton: UIButton {
    init(filter: FilterTag, identifier: String) {
        super.init(frame: .zero)
        setTitle(filter.title, for: .normal)
        titleLabel?.font = .systemFont(ofSize: 13)
        layer.cornerRadius = 15
        layer.borderWidth = 0.5
        accessibilityIdentifier = "\(identifier).tag.\(filter.id)"
    }

    @available(*, unavailable)
    public required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(selected: Bool, theme: PageTheme) {
        backgroundColor = UIColor(selected ? theme.buttonSelected : theme.chip)
        layer.borderColor = UIColor(selected ? theme.buttonSelected : theme.border).cgColor
        setTitleColor(UIColor(theme.ink), for: .normal)
        accessibilityTraits = selected ? [.button, .selected] : .button
    }
}
#endif
