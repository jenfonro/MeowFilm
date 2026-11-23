// SPDX-License-Identifier: GPL-3.0-only
#if canImport(UIKit)
import UIKit
import MeowUI

@MainActor
final class MusicChannelTagsView: UIView {
    private enum Geometry {
        static let multiRowBottomInset: CGFloat = 8
        static let expandedCornerRadius: CGFloat = 16
    }

    private struct FilterGroup {
        let key: String
        let items: [MusicPublicBrowseFilter]
    }

    var onExpansionChanged: (() -> Void)?
    var isExpanded: Bool { rows.contains(where: \.isExpanded) }

    var collapsedHeight: CGFloat {
        guard !rows.isEmpty else { return 0 }
        let total = rows.reduce(CGFloat.zero) { $0 + $1.collapsedHeight }
        guard total > 0 else { return 0 }
        return isMultiDimension ? total + Geometry.multiRowBottomInset : total
    }

    private let themeProvider: any PageThemeProviding
    private let expandedBackground: WindowGradientView
    private var rows: [FilterTagsView] = []
    private var rowKeys: [String] = []
    private var isMultiDimension = false
    private var lastWidth: CGFloat = 0

    init(themeProvider: any PageThemeProviding) {
        self.themeProvider = themeProvider
        expandedBackground = WindowGradientView(themeProvider: themeProvider)
        super.init(frame: .zero)
        layer.maskedCorners = [.layerMinXMaxYCorner, .layerMaxXMaxYCorner]
        expandedBackground.isHidden = true
        expandedBackground.accessibilityElementsHidden = true
        addSubview(expandedBackground)
        expandedBackground.pinEdges(to: self)
        accessibilityIdentifier = "music.channel.tags"
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func collapse() {
        var changed = false
        for row in rows where row.isExpanded {
            row.collapse()
            changed = true
        }
        if changed {
            applyExpandedAppearance()
            setNeedsLayout()
        }
    }

    func toggleRowExpansion(at index: Int = 0) {
        guard rows.indices.contains(index) else { return }
        rows[index].toggleExpansion()
    }

    func configure(filters: [MusicPublicBrowseFilter], selectedID: String, theme: PageTheme,
                   onSelect: @escaping (String) -> Void) {
        let visible = filters.filter { !$0.title.isEmpty }
        guard visible.count > 1 else {
            clearRows()
            applyExpandedAppearance()
            setNeedsLayout()
            return
        }

        let groups = Self.grouped(visible)
        let selectedGroupCount = groups.filter { $0.items.contains(where: \.selected) }.count
        let multiDimension = groups.count > 1 && selectedGroupCount > 1

        if multiDimension {
            let keys = groups.map(\.key)
            if !isMultiDimension || rowKeys != keys || rows.count != groups.count {
                rebuildRows(keys: keys, multiDimension: true)
            }
            let matchingSelectedGroups = groups.enumerated().compactMap { index, group -> Int? in
                group.items.contains(where: { $0.id == selectedID }) ? index : nil
            }
            let optimisticGroupIndex = matchingSelectedGroups.count == 1 ? matchingSelectedGroups[0] : nil
            for (index, group) in groups.enumerated() {
                let tags = group.items.map { item -> FilterTag in
                    let selected: Bool
                    if let optimisticGroupIndex {
                        selected = index == optimisticGroupIndex ? item.id == selectedID : item.selected
                    } else {
                        selected = item.selected
                    }
                    return FilterTag(id: item.id, title: item.title, group: item.group, isSelected: selected)
                }
                rows[index].configure(filters: tags, selectedID: selectedID, theme: theme) { [weak self] id in
                    self?.collapse()
                    onSelect(id)
                }
            }
        } else {
            if isMultiDimension || rows.count != 1 {
                rebuildRows(keys: [""], multiDimension: false)
            }
            let useExplicitSelection = visible.contains(where: \.selected) &&
                visible.contains(where: { $0.selected && $0.id == selectedID })
            let tags = visible.map { item in
                FilterTag(
                    id: item.id,
                    title: item.title,
                    group: item.group,
                    isSelected: useExplicitSelection ? item.selected : (item.id == selectedID)
                )
            }
            rows[0].configure(filters: tags, selectedID: selectedID, theme: theme, onSelect: onSelect)
        }

        if lastWidth > 0 { updateLayout(width: lastWidth) }
        applyExpandedAppearance()
        setNeedsLayout()
    }

    func updateLayout(width: CGFloat) {
        lastWidth = width
        for row in rows {
            row.updateLayout(width: width)
        }
        applyExpandedAppearance()
        setNeedsLayout()
    }

    func preferredHeight(for width: CGFloat) -> CGFloat {
        guard !rows.isEmpty, width > 0 else { return 0 }
        guard isMultiDimension else {
            return rows[0].preferredHeight(for: width)
        }
        guard let expandedIndex = rows.firstIndex(where: \.isExpanded) else {
            return collapsedHeight
        }
        let expandedTop = rows.prefix(expandedIndex).reduce(CGFloat.zero) { $0 + $1.collapsedHeight }
        let expandedHeight = rows[expandedIndex].preferredHeight(for: width)
        return max(collapsedHeight, expandedTop + expandedHeight)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        guard !rows.isEmpty, bounds.width > 0 else { return }
        if !isMultiDimension {
            rows[0].isHidden = rows[0].collapsedHeight == 0
            rows[0].frame = bounds
            return
        }
        let expandedIndex = rows.firstIndex(where: \.isExpanded)
        var y: CGFloat = 0
        for (index, row) in rows.enumerated() {
            if let expandedIndex, index > expandedIndex {
                row.isHidden = true
                row.frame = .zero
                continue
            }
            row.isHidden = row.collapsedHeight == 0
            if let expandedIndex, index == expandedIndex {
                let height = row.preferredHeight(for: bounds.width)
                row.frame = CGRect(x: 0, y: y, width: bounds.width, height: height)
                bringSubviewToFront(row)
                y += height
            } else {
                let height = row.collapsedHeight
                row.frame = CGRect(x: 0, y: y, width: bounds.width, height: height)
                y += height
            }
        }
    }

    private func rebuildRows(keys: [String], multiDimension: Bool) {
        clearRows()
        isMultiDimension = multiDimension
        rowKeys = keys
        for (index, key) in keys.enumerated() {
            let suffix = key.isEmpty ? "music.channel" : "music.channel.\(key)"
            let row = FilterTagsView(
                themeProvider: themeProvider,
                identifier: suffix,
                compact: multiDimension,
                showsExpandedBackground: !multiDimension
            )
            row.onExpansionChanged = { [weak self] in
                self?.rowExpansionChanged(at: index)
            }
            addSubview(row)
            rows.append(row)
        }
    }

    private func clearRows() {
        for row in rows { row.removeFromSuperview() }
        rows = []
        rowKeys = []
        isMultiDimension = false
    }

    private func rowExpansionChanged(at index: Int) {
        if rows.indices.contains(index), rows[index].isExpanded {
            for (otherIndex, other) in rows.enumerated() where otherIndex != index && other.isExpanded {
                other.collapse()
            }
        }
        applyExpandedAppearance()
        setNeedsLayout()
        onExpansionChanged?()
    }

    private func applyExpandedAppearance() {
        let showContainerSurface = isMultiDimension && isExpanded
        expandedBackground.isHidden = !showContainerSurface
        backgroundColor = .clear
        layer.cornerRadius = showContainerSurface ? Geometry.expandedCornerRadius : 0
        clipsToBounds = showContainerSurface
    }

    private static func grouped(_ filters: [MusicPublicBrowseFilter]) -> [FilterGroup] {
        var order: [String] = []
        var buckets: [String: [MusicPublicBrowseFilter]] = [:]
        for item in filters {
            if buckets[item.group] == nil {
                order.append(item.group)
            }
            buckets[item.group, default: []].append(item)
        }
        return order.map { FilterGroup(key: $0, items: buckets[$0] ?? []) }
    }
}
#endif
