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
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                if state.busy { ProgressView("正在处理") }
                if state.canRetry { Button("重试", action: retry) }
                if !state.categories.isEmpty {
                    MusicDailyStyleTagLayout {
                        ForEach(state.categories) { category in
                            chip(category.name, selected: category.id == state.category) {
                                selectCategory(category.id)
                            }
                            .accessibilityIdentifier("music.dailyStyle.category.\(category.id)")
                        }
                    }
                }
                if let category = state.categories.first(where: { $0.id == state.category }) {
                    MusicDailyStyleTagLayout {
                        ForEach(category.tags) { tag in
                            chip(tag.name, selected: state.selected.contains(tag.id)) {
                                selectTag(tag.id)
                            }
                            .accessibilityIdentifier("music.dailyStyle.tag.\(tag.id)")
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(16)
        }
        .background(Color.clear)
        .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
        .tint(Color(uiColor: UIColor(theme.accent)))
    }

    private func chip(_ title: String, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .lineLimit(1)
                .font(.system(size: 13))
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
                .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
                .background(Color(uiColor: UIColor(selected ? theme.buttonSelected : theme.chip)),
                            in: Capsule())
                .overlay(Capsule().strokeBorder(
                    Color(uiColor: UIColor(selected ? theme.buttonSelected : theme.border)), lineWidth: 0.5))
        }
        .buttonStyle(.plain)
        .disabled(!state.available || state.busy)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

/// Uses the same wrapping geometry as the channel filters, while the editor owns its draft.
private struct MusicDailyStyleTagLayout: Layout {
    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        rows(proposal: proposal, subviews: subviews).size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let rows = rows(proposal: ProposedViewSize(width: bounds.width, height: nil), subviews: subviews)
        for (view, frame) in zip(subviews, rows.frames) {
            view.place(at: CGPoint(x: bounds.minX + frame.minX, y: bounds.minY + frame.minY),
                       anchor: .topLeading,
                       proposal: ProposedViewSize(width: frame.width, height: frame.height))
        }
    }

    private func rows(proposal: ProposedViewSize, subviews: Subviews) -> FilterTagRows {
        FilterTagRows(width: proposal.width, items: subviews.map { $0.sizeThatFits(.unspecified) })
    }
}
#endif
