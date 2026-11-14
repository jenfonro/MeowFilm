#if os(iOS)
import UIKit
import SwiftUI
import MeowUI

@MainActor
final class MusicChannelsPickerViewController: HostingPageViewController<MusicChannelsPickerContent> {
    private var channels: [MusicPublicBrowseChannel]
    private var selectedID: String?
    private var emptyTitle: String
    private let onSelect: (MusicPublicBrowseChannel) -> Void

    var preferredHeight: CGFloat {
        MusicChannelsPickerContent.headerHeight
            + MusicChannelsPickerContent.bodyHeight(channelCount: channels.count)
    }

    init(channels: [MusicPublicBrowseChannel], selectedID: String?, emptyTitle: String,
         appearance: MusicAppearancePreferencesStore, onSelect: @escaping (MusicPublicBrowseChannel) -> Void) {
        self.channels = channels; self.selectedID = selectedID; self.emptyTitle = emptyTitle
        self.onSelect = onSelect
        super.init(themeProvider: appearance, content: MusicChannelsPickerContent(
            channels: channels, selectedID: selectedID, emptyTitle: emptyTitle,
            theme: PageTheme(colors: appearance.pagePalette), close: {}, select: { _ in }))
    }

    override var usesPageHeader: Bool { false }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.layer.cornerRadius = 24
        view.layer.maskedCorners = [.layerMinXMaxYCorner, .layerMaxXMaxYCorner]
        view.clipsToBounds = true
        view.accessibilityIdentifier = "music.channels.picker"
    }

    override func applyTheme() {
        super.applyTheme()
        renderContent()
    }

    func update(channels: [MusicPublicBrowseChannel], selectedID: String?, emptyTitle: String) {
        self.channels = channels; self.selectedID = selectedID; self.emptyTitle = emptyTitle
        renderContent()
    }

    private func renderContent() {
        setContent(MusicChannelsPickerContent(channels: channels, selectedID: selectedID,
            emptyTitle: emptyTitle, theme: theme,
            close: { [weak self] in self?.dismiss(animated: true) },
            select: { [weak self] channel in
                guard let self else { return }
                self.dismiss(animated: true) { [onSelect = self.onSelect] in onSelect(channel) }
            }))
    }
}

struct MusicChannelsPickerContent: View {
    let channels: [MusicPublicBrowseChannel]
    let selectedID: String?
    let emptyTitle: String
    let theme: PageTheme
    let close: () -> Void
    let select: (MusicPublicBrowseChannel) -> Void

    static let rowHeight: CGFloat = 54
    static let gridSpacing: CGFloat = 12
    static let bottomPadding: CGFloat = 24
    static let headerHeight: CGFloat = 64

    static func bodyHeight(channelCount: Int) -> CGFloat {
        guard channelCount > 0 else { return 96 }
        let rows = (channelCount + 2) / 3
        return CGFloat(rows) * rowHeight
            + CGFloat(rows - 1) * gridSpacing
            + bottomPadding
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                Button(action: close) {
                    Image(systemName: "xmark")
                        .font(.system(size: 17, weight: .semibold))
                        .foregroundStyle(Color(UIColor(theme.muted)))
                        .frame(width: 44, height: 44)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("关闭")
                .accessibilityIdentifier("music.channels.picker.close")
                Text("选择频道")
                    .font(.system(size: 20))
                    .foregroundStyle(Color(UIColor(theme.ink)))
                    .frame(maxWidth: .infinity)
                Color.clear
                    .frame(width: 44, height: 44)
                    .accessibilityHidden(true)
            }
            .padding(.horizontal, 16)
            .padding(.top, 8)
            .padding(.bottom, 12)
            if channels.isEmpty {
                Text(emptyTitle)
                    .font(.system(size: 14))
                    .foregroundStyle(Color(UIColor(theme.muted)))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: Self.gridSpacing), count: 3),
                              spacing: Self.gridSpacing) {
                        ForEach(channels, id: \.identity) { channel in
                            let selected = channel.identity == selectedID
                            Button {
                                select(channel)
                            } label: {
                                Text(channel.title)
                                    .font(.system(size: 13))
                                    .foregroundStyle(Color(UIColor(selected ? theme.accent : theme.ink)))
                                    .multilineTextAlignment(.center)
                                    .lineLimit(2)
                                    .frame(maxWidth: .infinity, minHeight: Self.rowHeight)
                                    .padding(.horizontal, 6)
                                    .background(Color(UIColor(
                                        selected ? theme.buttonSelected : theme.buttonSurface
                                    )))
                                    .overlay {
                                        RoundedRectangle(cornerRadius: 13)
                                            .stroke(Color(UIColor(selected ? theme.accent : theme.border)),
                                                    lineWidth: 1)
                                    }
                                    .clipShape(RoundedRectangle(cornerRadius: 13))
                            }
                            .buttonStyle(.plain)
                            .accessibilityIdentifier("music.channel.\(channel.identity)")
                            .accessibilityAddTraits(selected ? .isSelected : [])
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.bottom, Self.bottomPadding)
                }
                .mask {
                    GeometryReader { proxy in
                        let fade = Self.bottomPadding
                        let solid = proxy.size.height > fade
                            ? (proxy.size.height - fade) / proxy.size.height
                            : 0
                        LinearGradient(
                            stops: [.init(color: .black, location: 0),
                                    .init(color: .black, location: solid),
                                    .init(color: .clear, location: 1)],
                            startPoint: .top, endPoint: .bottom)
                    }
                }
            }
        }
    }
}
#endif
