// SPDX-License-Identifier: GPL-3.0-only
#if canImport(UIKit)
import UIKit

@MainActor
open class FilterTagsView: UIView, UIScrollViewDelegate {
    private enum Geometry {
        static let inset: CGFloat = 16
        static let chipHeight: CGFloat = 30
        static let chipMinWidth: CGFloat = 36
        static let chipHorizontalPadding: CGFloat = 24
        static let chipSpacing: CGFloat = 8
        static let rowTop: CGFloat = 4
        static let rowSpacing: CGFloat = 10
        static let groupHeaderHeight: CGFloat = 18
        static let groupHeaderTopSpacing: CGFloat = 12
        static let groupHeaderBottomSpacing: CGFloat = 6
        static let toggleSize: CGFloat = 44
        static let toggleIconSize: CGFloat = 18
        static let toggleGap: CGFloat = 8
        static let edgeFadeWidth: CGFloat = 18
        static let collapsedHeight: CGFloat = 56
        static let compactCollapsedHeight: CGFloat = 40
        static let maximumExpandedHeight: CGFloat = 260
        static let expandedBottomInset: CGFloat = 16
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

    private struct ExpandedLayout {
        let size: CGSize
        let itemFrames: [CGRect]
        let headerFrames: [CGRect]
    }

    private struct TagSection {
        let title: String
        let indices: [Int]
    }

    public var onExpansionChanged: (() -> Void)?
    public private(set) var isExpanded = false
    public private(set) var hasOverflow = false
    private let scrollContainer = UIView()
    private let scroll = UIScrollView()
    private let edgeFadeMask = CAGradientLayer()
    private let toggle = UIButton(type: .system)
    private let expandedBackground: WindowGradientView
    private let identifier: String
    private let compact: Bool
    private let showsExpandedBackground: Bool
    private var filters: [FilterTag] = []
    private var buttons: [FilterTagButton] = []
    private var groupSections: [TagSection] = []
    private var groupLabels: [UILabel] = []
    private var selectedID = ""
    private var onSelect: ((String) -> Void)?
    private var layoutWidth: CGFloat?
    private var chipSizes: [CGSize] = []
    private var revealSelection = false
    private var lastEdgeFade: (size: CGSize, active: Bool, left: Bool, right: Bool)?

    public var collapsedHeight: CGFloat {
        guard filters.count > 1 else { return 0 }
        return compact ? Geometry.compactCollapsedHeight : Geometry.collapsedHeight
    }

    public init(themeProvider: any PageThemeProviding, identifier: String,
                compact: Bool = false, showsExpandedBackground: Bool = true) {
        self.identifier = identifier
        self.compact = compact
        self.showsExpandedBackground = showsExpandedBackground
        expandedBackground = WindowGradientView(themeProvider: themeProvider)
        super.init(frame: .zero)
        scroll.showsHorizontalScrollIndicator = false
        scroll.showsVerticalScrollIndicator = false
        scroll.alwaysBounceHorizontal = false
        scroll.isDirectionalLockEnabled = true
        scroll.contentInsetAdjustmentBehavior = .never
        scroll.delegate = self
        scrollContainer.clipsToBounds = true
        scrollContainer.addSubview(scroll)
        edgeFadeMask.startPoint = CGPoint(x: 0, y: 0.5)
        edgeFadeMask.endPoint = CGPoint(x: 1, y: 0.5)
        layer.maskedCorners = [.layerMinXMaxYCorner, .layerMaxXMaxYCorner]
        toggle.setImage(PageIcon.image(.down, size: Geometry.toggleIconSize), for: .normal)
        toggle.accessibilityIdentifier = "\(identifier).tags.toggle"
        toggle.isHidden = true
        toggle.addAction(UIAction { [weak self] _ in
            self?.toggleExpansion()
        }, for: .touchUpInside)
        expandedBackground.isHidden = true
        expandedBackground.accessibilityElementsHidden = true
        addSubview(expandedBackground)
        addSubview(scrollContainer)
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

    public func toggleExpansion() {
        updateLayout(width: bounds.width)
        guard hasOverflow else { return }
        setExpanded(!isExpanded, notify: true)
    }

    public func configure(filters: [FilterTag], selectedID: String, theme: PageTheme,
                          onSelect: @escaping (String) -> Void) {
        let structureChanged = filters.count != self.filters.count ||
            zip(filters, self.filters).contains { $0.title != $1.title || $0.group != $1.group }
        let previousSelectedIndex = selectedIndex(in: self.filters, selectedID: self.selectedID)
        let nextSelectedIndex = selectedIndex(in: filters, selectedID: selectedID)
        let selectionChanged = structureChanged || previousSelectedIndex != nextSelectedIndex
        self.filters = filters
        self.selectedID = selectedID
        self.onSelect = onSelect
        if structureChanged {
            let font = UIFont.systemFont(ofSize: 13)
            chipSizes = filters.map {
                let textWidth = ceil(($0.title as NSString).size(withAttributes: [.font: font]).width)
                return CGSize(
                    width: max(Geometry.chipMinWidth, textWidth + Geometry.chipHorizontalPadding),
                    height: Geometry.chipHeight
                )
            }
            groupSections = Self.buildGroupSections(filters)
            scroll.setContentOffset(.zero, animated: false)
            for label in groupLabels { label.removeFromSuperview() }
            groupLabels = []
            if !groupSections.isEmpty {
                for section in groupSections {
                    let label = UILabel()
                    label.text = section.title
                    label.font = .systemFont(ofSize: 12)
                    label.isHidden = true
                    scroll.addSubview(label)
                    groupLabels.append(label)
                }
            }
            for button in buttons { button.removeFromSuperview() }
            buttons = []
            buttons.reserveCapacity(filters.count)
            for (index, filter) in filters.enumerated() {
                let button = FilterTagButton(filter: filter, identifier: identifier)
                button.addAction(UIAction { [weak self] _ in
                    guard let self, self.filters.indices.contains(index) else { return }
                    let targetID = self.filters[index].id
                    self.collapse()
                    self.onSelect?(targetID)
                }, for: .touchUpInside)
                buttons.append(button)
                scroll.addSubview(button)
            }
        }
        for (index, filter) in filters.enumerated() where buttons.indices.contains(index) {
            let selected = isSelected(filter, selectedID: selectedID)
            buttons[index].configure(filter: filter, identifier: identifier, selected: selected, theme: theme)
        }
        for label in groupLabels {
            label.textColor = UIColor(theme.muted)
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
        let expanded = expandedLayout(metrics: metrics)
        return min(Geometry.maximumExpandedHeight, expanded.size.height + Geometry.expandedVerticalPadding)
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        guard filters.count > 1, bounds.width > 0 else {
            scrollContainer.frame = .zero
            scroll.frame = .zero
            toggle.bounds = .zero
            toggle.center = .zero
            scroll.isScrollEnabled = false
            updateEdgeFade()
            return
        }
        let metrics = layoutMetrics(width: bounds.width)
        if isExpanded {
            let expanded = expandedLayout(metrics: metrics)
            let visibleHeight = max(0, bounds.height - Geometry.expandedVerticalPadding)
            scrollContainer.frame = CGRect(x: Geometry.inset, y: Geometry.rowTop,
                                           width: metrics.chipWidth, height: visibleHeight)
            scroll.frame = scrollContainer.bounds
            scroll.contentSize = expanded.size
            for (index, frame) in expanded.itemFrames.enumerated() where buttons.indices.contains(index) {
                buttons[index].frame = frame
            }
            for (index, label) in groupLabels.enumerated() {
                if expanded.headerFrames.indices.contains(index), !groupSections[index].title.isEmpty {
                    label.frame = expanded.headerFrames[index]
                    label.isHidden = false
                } else {
                    label.frame = .zero
                    label.isHidden = true
                }
            }
            scroll.isScrollEnabled = expanded.size.height > visibleHeight + 0.5
        } else {
            scrollContainer.frame = CGRect(x: Geometry.inset, y: Geometry.rowTop,
                                           width: metrics.chipWidth, height: Geometry.chipHeight)
            scroll.frame = scrollContainer.bounds
            for label in groupLabels {
                label.frame = .zero
                label.isHidden = true
            }
            var x: CGFloat = 0
            for (index, size) in metrics.sizes.enumerated() where buttons.indices.contains(index) {
                buttons[index].frame = CGRect(x: x, y: 0, width: size.width, height: size.height)
                x += size.width + Geometry.chipSpacing
            }
            let contentWidth = max(0, x - Geometry.chipSpacing)
            scroll.contentSize = CGSize(width: contentWidth, height: Geometry.chipHeight)
            scroll.isScrollEnabled = metrics.overflow
            if revealSelection {
                revealSelection = false
                revealSelectedChip(contentWidth: contentWidth)
            } else if scroll.contentOffset.x > max(0, contentWidth - scroll.bounds.width) {
                scroll.setContentOffset(CGPoint(x: max(0, contentWidth - scroll.bounds.width), y: 0), animated: false)
            }
        }
        if metrics.overflow {
            let rect = toggleFrame(width: bounds.width)
            toggle.bounds = CGRect(origin: .zero, size: rect.size)
            toggle.center = CGPoint(x: rect.midX, y: rect.midY)
        } else {
            toggle.bounds = .zero
            toggle.center = .zero
        }
        updateEdgeFade()
    }

    public func scrollViewDidScroll(_ scrollView: UIScrollView) {
        updateEdgeFade()
    }

    private func revealSelectedChip(contentWidth: CGFloat) {
        let maxOffset = max(0, contentWidth - scroll.bounds.width)
        guard maxOffset > 0.5,
              let index = selectedIndex(in: filters, selectedID: selectedID),
              buttons.indices.contains(index) else {
            if scroll.contentOffset.x != 0 {
                scroll.setContentOffset(.zero, animated: false)
            }
            return
        }
        let selectedFrame = buttons[index].frame
        if selectedFrame.maxX <= scroll.bounds.width {
            if scroll.contentOffset.x != 0 {
                scroll.setContentOffset(.zero, animated: false)
            }
            return
        }
        let targetX = min(maxOffset, max(0, selectedFrame.midX - scroll.bounds.width / 2))
        scroll.setContentOffset(CGPoint(x: targetX, y: 0), animated: false)
    }

    private func updateEdgeFade() {
        let width = scrollContainer.bounds.width
        let active = !isExpanded && hasOverflow && width > Geometry.edgeFadeWidth * 2
        let canScrollLeft = active && scroll.contentOffset.x > 1
        let canScrollRight = active && (scroll.contentOffset.x + scroll.bounds.width < scroll.contentSize.width - 1)
        let shouldMask = canScrollLeft || canScrollRight
        if let lastEdgeFade,
           lastEdgeFade.size == scrollContainer.bounds.size,
           lastEdgeFade.active == shouldMask,
           lastEdgeFade.left == canScrollLeft,
           lastEdgeFade.right == canScrollRight {
            return
        }
        lastEdgeFade = (scrollContainer.bounds.size, shouldMask, canScrollLeft, canScrollRight)
        guard shouldMask else {
            if scrollContainer.layer.mask != nil { scrollContainer.layer.mask = nil }
            return
        }
        let fadeFraction = min(0.2, Double(Geometry.edgeFadeWidth / width))
        let clear = UIColor.clear.cgColor
        let opaque = UIColor.black.cgColor
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        edgeFadeMask.frame = scrollContainer.bounds
        edgeFadeMask.colors = [
            canScrollLeft ? clear : opaque,
            opaque,
            opaque,
            canScrollRight ? clear : opaque,
        ]
        edgeFadeMask.locations = [
            0,
            NSNumber(value: fadeFraction),
            NSNumber(value: 1 - fadeFraction),
            1,
        ]
        if scrollContainer.layer.mask !== edgeFadeMask {
            scrollContainer.layer.mask = edgeFadeMask
        }
        CATransaction.commit()
    }

    private func layoutMetrics(width: CGFloat) -> LayoutMetrics {
        let sizes = chipSizes
        let intrinsic = sizes.reduce(CGFloat.zero) { $0 + $1.width } +
            CGFloat(max(0, sizes.count - 1)) * Geometry.chipSpacing
        let fullWidth = max(1, width - Geometry.inset * 2)
        let overflow = intrinsic > fullWidth + 1
        let chipWidth = overflow
            ? max(1, fullWidth - Geometry.toggleLeadingExtent - Geometry.toggleGap)
            : fullWidth
        return LayoutMetrics(sizes: sizes, chipWidth: chipWidth, overflow: overflow)
    }

    private func expandedLayout(metrics: LayoutMetrics) -> ExpandedLayout {
        guard !groupSections.isEmpty else {
            let rows = FilterTagRows(
                width: metrics.chipWidth, items: metrics.sizes, rowSpacing: Geometry.rowSpacing
            )
            return ExpandedLayout(size: rows.size, itemFrames: rows.frames, headerFrames: [])
        }
        var itemFrames = Array(repeating: CGRect.zero, count: filters.count)
        var headerFrames: [CGRect] = []
        headerFrames.reserveCapacity(groupSections.count)
        var y: CGFloat = 0
        for (sectionIndex, section) in groupSections.enumerated() {
            if sectionIndex > 0 {
                y += Geometry.groupHeaderTopSpacing
            }
            if !section.title.isEmpty {
                headerFrames.append(CGRect(x: 0, y: y, width: metrics.chipWidth, height: Geometry.groupHeaderHeight))
                y += Geometry.groupHeaderHeight + Geometry.groupHeaderBottomSpacing
            } else {
                headerFrames.append(.zero)
            }
            let sectionSizes = section.indices.map { metrics.sizes[$0] }
            let rows = FilterTagRows(
                width: metrics.chipWidth, items: sectionSizes, rowSpacing: Geometry.rowSpacing
            )
            for (offset, itemIndex) in section.indices.enumerated() where rows.frames.indices.contains(offset) {
                itemFrames[itemIndex] = rows.frames[offset].offsetBy(dx: 0, dy: y)
            }
            y += rows.size.height
        }
        return ExpandedLayout(
            size: CGSize(width: metrics.chipWidth, height: y),
            itemFrames: itemFrames,
            headerFrames: headerFrames
        )
    }

    private static func buildGroupSections(_ filters: [FilterTag]) -> [TagSection] {
        var order: [String] = []
        var grouped: [String: [Int]] = [:]
        for (index, filter) in filters.enumerated() {
            if grouped[filter.group] == nil {
                order.append(filter.group)
            }
            grouped[filter.group, default: []].append(index)
        }
        guard order.count > 1, order.contains(where: { !$0.isEmpty }) else { return [] }
        return order.map { TagSection(title: $0, indices: grouped[$0] ?? []) }
    }

    private func isSelected(_ filter: FilterTag, selectedID: String) -> Bool {
        if let explicit = filter.isSelected { return explicit }
        return filter.id == selectedID
    }

    private func selectedIndex(in filters: [FilterTag], selectedID: String) -> Int? {
        if filters.contains(where: { $0.isSelected != nil }) {
            return filters.firstIndex(where: { $0.isSelected == true })
        }
        return filters.firstIndex(where: { $0.id == selectedID })
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
        let showSurface = isExpanded && showsExpandedBackground
        expandedBackground.isHidden = !showSurface
        backgroundColor = .clear
        layer.cornerRadius = showSurface ? Geometry.expandedCornerRadius : 0
        clipsToBounds = showSurface
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

    func configure(filter: FilterTag, identifier: String, selected: Bool, theme: PageTheme) {
        if title(for: .normal) != filter.title {
            setTitle(filter.title, for: .normal)
        }
        accessibilityIdentifier = "\(identifier).tag.\(filter.id)"
        backgroundColor = UIColor(selected ? theme.buttonSelected : theme.chip)
        layer.borderColor = UIColor(selected ? theme.buttonSelected : theme.border).cgColor
        setTitleColor(UIColor(theme.ink), for: .normal)
        accessibilityTraits = selected ? [.button, .selected] : .button
    }
}
#endif
