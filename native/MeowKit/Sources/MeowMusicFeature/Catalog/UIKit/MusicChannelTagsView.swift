// SPDX-License-Identifier: GPL-3.0-only
#if canImport(UIKit)
import Foundation
import MeowUI

@MainActor
final class MusicChannelTagsView: FilterTagsView {
    init(themeProvider: any PageThemeProviding) {
        super.init(themeProvider: themeProvider, identifier: "music.channel")
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(filters: [MusicPublicBrowseFilter], selectedID: String, theme: PageTheme,
                   onSelect: @escaping (String) -> Void) {
        super.configure(filters: filters.map { FilterTag(id: $0.id, title: $0.title) },
                        selectedID: selectedID, theme: theme, onSelect: onSelect)
    }
}
#endif
