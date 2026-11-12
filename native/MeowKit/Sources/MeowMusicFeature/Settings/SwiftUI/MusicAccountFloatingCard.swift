#if os(iOS)
import SwiftUI
import UIKit
import MeowUI

/// Dims the presenting page; account cards are presented over it instead of being pushed.
final class MusicAccountFloatingBackdrop: UIView {
    init() {
        super.init(frame: .zero)
        backgroundColor = UIColor.black.withAlphaComponent(0.3)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}

struct MusicAccountFloatingCard<Content: View>: View {
    let theme: PageTheme
    @ViewBuilder let content: () -> Content

    var body: some View {
        GeometryReader { proxy in
            ScrollView {
                content()
                    .padding(20)
                    .frame(maxWidth: 420, alignment: .leading)
                    .background(
                        RoundedRectangle(cornerRadius: 24, style: .continuous)
                            .fill(Color(uiColor: UIColor(theme.settings.surface)))
                    )
                    .shadow(color: .black.opacity(0.12), radius: 24, y: 8)
                    .padding(16)
                    .frame(maxWidth: .infinity, minHeight: proxy.size.height)
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .foregroundStyle(Color(uiColor: UIColor(theme.settings.ink)))
        .tint(Color(uiColor: UIColor(theme.settings.accent)))
    }
}

struct MusicAccountCardHeader<Trailing: View>: View {
    let platform: MusicAccountPlatform
    let title: String
    let theme: PageTheme
    let close: () -> Void
    @ViewBuilder let trailing: () -> Trailing

    var body: some View {
        HStack(spacing: 10) {
            MusicPlatformLogo(assetName: platform.settingsLogoName, theme: theme)
            Text(title)
                .font(.title3.weight(.semibold))
                .fixedSize(horizontal: false, vertical: true)
                .accessibilityAddTraits(.isHeader)
            Spacer(minLength: 4)
            trailing()
            Button(action: close) {
                Image(systemName: "xmark")
                    .font(.body.weight(.semibold))
                    .foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
                    .frame(width: 44, height: 44)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("关闭")
            .padding(.trailing, -10)
        }
    }
}

/// Segmented input: one capsule keeps paired values (region and number, code and send) together.
struct MusicAccountInputCapsule<Content: View>: View {
    let theme: PageTheme
    @ViewBuilder let content: () -> Content

    var body: some View {
        HStack(spacing: 0) { content() }
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Capsule().fill(Color(uiColor: UIColor(theme.settings.input))))
            .overlay(Capsule().strokeBorder(Color(uiColor: UIColor(theme.settings.inputBorder))))
    }
}

struct MusicAccountPrimaryButtonStyle: ButtonStyle {
    let theme: PageTheme

    func makeBody(configuration: Configuration) -> some View {
        MusicAccountButtonBody(configuration: configuration, theme: theme, destructive: false)
    }
}

struct MusicAccountDestructiveButtonStyle: ButtonStyle {
    let theme: PageTheme

    func makeBody(configuration: Configuration) -> some View {
        MusicAccountButtonBody(configuration: configuration, theme: theme, destructive: true)
    }
}

private struct MusicAccountButtonBody: View {
    @Environment(\.isEnabled) private var isEnabled
    let configuration: ButtonStyleConfiguration
    let theme: PageTheme
    let destructive: Bool

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        configuration.label
            .font(.body.weight(.semibold))
            .lineLimit(1)
            .minimumScaleFactor(0.8)
            .frame(maxWidth: .infinity, minHeight: 48)
            .foregroundStyle(foreground)
            .background(shape.fill(fill))
            .overlay {
                if destructive { shape.strokeBorder(Color(uiColor: UIColor(theme.settings.inputBorder))) }
            }
            .contentShape(shape)
            .opacity(configuration.isPressed ? 0.8 : 1)
    }

    private var foreground: Color {
        if !isEnabled { return Color(uiColor: UIColor(theme.settings.muted)) }
        return destructive ? theme.settingsDanger : Color(uiColor: UIColor(theme.settings.onAccent))
    }

    private var fill: Color {
        if destructive { return Color.clear }
        return Color(uiColor: UIColor(isEnabled ? theme.settings.accent : theme.settings.input))
    }
}

extension PageTheme {
    /// Error and destructive red shared by account rows, details and confirmations.
    var settingsDanger: Color { Color(uiColor: UIColor(settings.danger)) }
}
#endif
