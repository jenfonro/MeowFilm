#if os(iOS)
import UIKit
import SwiftUI
import MeowUI

struct MusicDailyStyleState: Equatable {
    var categories: [MusicDailyStyleCategory] = []
    var category = ""
    var selected = Set<String>()
    var available = false
    var busy = false
    var canRetry = false
}

@MainActor
public struct MusicDailyStyleContent: View {
    var state = MusicDailyStyleState()
    var theme: PageTheme
    var selectCategory: (String) -> Void = { _ in }
    var selectTag: (String) -> Void = { _ in }
    var retry: () -> Void = {}

    public var body: some View {
        List {
            if state.busy {
                ProgressView("正在处理")
                    .listRowBackground(Color.clear)
            }
            if state.canRetry {
                Button("重试", action: retry)
                    .listRowBackground(Color.clear)
            }
            if !state.categories.isEmpty {
                Section("分类") {
                    ForEach(state.categories) { category in
                        Button { selectCategory(category.id) } label: {
                            selectionRow(category.name, selected: category.id == state.category)
                        }
                        .disabled(!state.available || state.busy)
                        .accessibilityAddTraits(category.id == state.category ? .isSelected : [])
                    }
                }
                .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
            }
            if let category = state.categories.first(where: { $0.id == state.category }) {
                Section(category.name) {
                    ForEach(category.tags) { tag in
                        Button { selectTag(tag.id) } label: {
                            selectionRow(tag.name, selected: state.selected.contains(tag.id))
                        }
                        .disabled(!state.available || state.busy)
                        .accessibilityIdentifier("music.dailyStyle.tag.\(tag.id)")
                        .accessibilityAddTraits(state.selected.contains(tag.id) ? .isSelected : [])
                    }
                }
                .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
            }
        }
        .listStyle(.insetGrouped)
        .scrollContentBackground(.hidden)
        .background(Color.clear)
        .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
        .tint(Color(uiColor: UIColor(theme.accent)))
    }

    private func selectionRow(_ title: String, selected: Bool) -> some View {
        HStack {
            Text(title).foregroundStyle(Color(uiColor: UIColor(theme.ink)))
            Spacer()
            if selected {
                Image(systemName: "checkmark").accessibilityHidden(true)
            }
        }
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }
}
#endif
