#if os(iOS)
import UIKit
import SwiftUI
import MeowUI

struct MusicFMSettingsState: Equatable {
    var active: MusicHomeAction?
    var available = false
    var pendingChoice: String?
}

@MainActor
public struct MusicFMSettingsContent: View {
    var state = MusicFMSettingsState()
    var theme: PageTheme
    var icons: [String: UIImage] = [:]
    var select: (MusicFMModeChoice) -> Void = { _ in }

    public var body: some View {
        ScrollView {
            VStack(spacing: 10) {
                ForEach(MusicFMModeChoice.main) { choice in
                    choiceButton(choice, scene: false)
                }
                Text("场景模式")
                    .font(.subheadline)
                    .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.top, 10)
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 4), count: 4),
                          spacing: 4) {
                    ForEach(MusicFMModeChoice.scenes) { choice in
                        choiceButton(choice, scene: true)
                    }
                }
            }
            .padding(14)
        }
        .background(Color.clear)
        .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
        .tint(Color(uiColor: UIColor(theme.accent)))
    }

    private func choiceButton(_ choice: MusicFMModeChoice, scene: Bool) -> some View {
        let selected = choice.matches(state.active)
        return Button { select(choice) } label: {
            VStack(spacing: 8) {
                if scene, let icon = icons[choice.submode] {
                    Image(uiImage: icon)
                        .renderingMode(.template)
                        .frame(width: 25, height: 25)
                        .accessibilityHidden(true)
                }
                Text(choice.title)
                    .font(scene ? .system(size: 12) : .system(size: 16, weight: .medium))
                    .multilineTextAlignment(.center)
                if state.pendingChoice == choice.id {
                    ProgressView().accessibilityLabel("正在切换")
                }
            }
            .frame(maxWidth: .infinity, minHeight: scene ? 84 : 64)
            .background(selected ? Color(uiColor: UIColor(theme.accent)).opacity(0.16) :
                scene ? Color.clear : Color(uiColor: UIColor(theme.panel)))
            .clipShape(RoundedRectangle(cornerRadius: 9))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!state.available || state.pendingChoice != nil)
        .accessibilityLabel(choice.title)
        .accessibilityValue(state.pendingChoice == choice.id ? "正在切换" : "")
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}
#endif
