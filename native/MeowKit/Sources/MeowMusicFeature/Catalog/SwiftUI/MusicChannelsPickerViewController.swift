#if os(iOS)
import UIKit
import SwiftUI
import MeowUI

@MainActor
final class MusicChannelsPickerViewController: HostingPageViewController<MusicChannelsPickerContent> {
    private var channels: [MusicPublicBrowseChannel]
    private var selectedID: String?
    private let onSelect: (MusicPublicBrowseChannel) -> Void

    init(channels: [MusicPublicBrowseChannel], selectedID: String?,
         appearance: MusicAppearancePreferencesStore, onSelect: @escaping (MusicPublicBrowseChannel) -> Void) {
        self.channels = channels; self.selectedID = selectedID; self.onSelect = onSelect
        super.init(themeProvider: appearance, content: MusicChannelsPickerContent(
            channels: channels, selectedID: selectedID, theme: PageTheme(palette: appearance.pageColorway),
            select: onSelect))
    }

    override func applyTheme() {
        super.applyTheme()
        renderContent()
    }

    func update(channels: [MusicPublicBrowseChannel], selectedID: String?) {
        self.channels = channels; self.selectedID = selectedID
        renderContent()
    }

    private func renderContent() {
        setContent(MusicChannelsPickerContent(channels: channels, selectedID: selectedID, theme: theme,
            select: { [weak self] channel in
                guard let self else { return }
                self.dismiss(animated: true) { [onSelect = self.onSelect] in onSelect(channel) }
            }))
    }
}

struct MusicChannelsPickerContent: View {
    let channels: [MusicPublicBrowseChannel]
    let selectedID: String?
    let theme: PageTheme
    let select: (MusicPublicBrowseChannel) -> Void

    var body: some View {
        List(channels, id: \.identity) { channel in
            Button {
                select(channel)
            } label: {
                HStack {
                    Text(channel.title)
                    Spacer()
                    if channel.identity == selectedID {
                        Image(systemName: "checkmark").foregroundStyle(Color(UIColor(theme.accent)))
                    }
                }
                .frame(minHeight: 36)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(Color(UIColor(theme.ink)))
            .listRowBackground(Color.clear)
            .accessibilityIdentifier("music.channel.\(channel.identity)")
            .accessibilityAddTraits(channel.identity == selectedID ? .isSelected : [])
        }
        .listStyle(.plain)
        .scrollContentBackground(.hidden)
    }
}
#endif
