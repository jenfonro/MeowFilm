#if os(iOS)
import SwiftUI
import UIKit

public struct SettingsListRowStyle: ViewModifier {
    public let theme: PageTheme
    public let first, last: Bool
    public init(theme: PageTheme, first: Bool, last: Bool) {
        self.theme = theme; self.first = first; self.last = last
    }
    public func body(content: Content) -> some View {
        content
            .font(.body.weight(.regular))
            .foregroundStyle(Color(uiColor: UIColor(theme.settings.ink)))
            .padding(.horizontal, SettingsVisualMetrics.rowHorizontalPadding)
            .padding(.vertical, SettingsVisualMetrics.rowVerticalPadding)
            .frame(maxWidth: .infinity, minHeight: SettingsVisualMetrics.rowHeight, alignment: .leading)
            .listRowInsets(EdgeInsets())
            .listRowSeparator(.hidden)
            .listRowBackground(SettingsRowBackground(theme: theme, first: first, last: last, showsSeparator: !last))
    }
}

public struct SettingsListSectionHeader: View {
    public let title: String
    public let theme: PageTheme
    public init(title: String, theme: PageTheme) { self.title = title; self.theme = theme }
    @ViewBuilder public var body: some View {
        // The list owns the gap above the title, including the first section.
        if #available(iOS 26.0, *) {
            titleText.listRowInsets(.top, 0)
        } else {
            titleText.listRowInsets(EdgeInsets(top: 0, leading: 16, bottom: 8, trailing: 16))
        }
    }
    private var titleText: some View {
        Text(title).font(.subheadline.weight(.regular))
            .foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
            .textCase(nil).fixedSize(horizontal: false, vertical: true)
    }
}

public struct SettingsListStyle: ViewModifier {
    public let theme: PageTheme
    public init(theme: PageTheme) { self.theme = theme }
    public func body(content: Content) -> some View {
        content.listStyle(.insetGrouped).scrollContentBackground(.hidden)
            .contentMargins(.top, SettingsVisualMetrics.contentTopSpacing, for: .scrollContent)
            .listSectionSpacing(SettingsVisualMetrics.sectionSpacing)
            .environment(\.defaultMinListRowHeight, SettingsVisualMetrics.rowHeight)
            .environment(\.defaultMinListHeaderHeight, 0)
            .font(.body.weight(.regular)).fontWeight(.regular).textCase(nil)
            .foregroundStyle(Color(uiColor: UIColor(theme.settings.ink)))
            .tint(Color(uiColor: UIColor(theme.settings.accent)))
            .toggleStyle(SettingsToggleStyle())
            .background(Color.clear)
    }
}

/// Both features use the same native List reordering; persistence remains a caller policy.
public struct SettingsOrderingSection<Item: Identifiable, Row: View>: View {
    private let title: String
    private let items: [Item]
    private let theme: PageTheme
    private let enabled: Bool
    private let row: (Item) -> Row
    private let move: (IndexSet, Int) -> Void
    public init(title: String, items: [Item], theme: PageTheme, enabled: Bool,
                @ViewBuilder row: @escaping (Item) -> Row, move: @escaping (IndexSet, Int) -> Void) {
        self.title = title; self.items = items; self.theme = theme
        self.enabled = enabled; self.row = row; self.move = move
    }
    public var body: some View {
        Section {
            ForEach(items) { item in
                row(item).disabled(!enabled).moveDisabled(!enabled)
                    .modifier(SettingsListRowStyle(theme: theme,
                        first: item.id == items.first?.id, last: item.id == items.last?.id))
            }.onMove(perform: move)
        } header: {
            SettingsListSectionHeader(title: title, theme: theme)
        }
        .listSectionSeparator(.hidden)
    }
}
#endif
