#if os(iOS)
import SwiftUI
import UIKit

/// Dims the presenting page; account cards are presented over it instead of being pushed.
public final class AccountFloatingBackdrop: UIView {
    public init() {
        super.init(frame: .zero)
        backgroundColor = UIColor.black.withAlphaComponent(0.3)
    }

    @available(*, unavailable)
    required public init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}

public struct AccountFloatingCard<Content: View>: View {
    public init(theme: PageTheme, @ViewBuilder content: @escaping () -> Content) {
        self.theme = theme; self.content = content
    }
    let theme: PageTheme
    @ViewBuilder let content: () -> Content

    public var body: some View {
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

public struct AccountCardHeader<Leading: View, Trailing: View>: View {
    private let title: String
    private let theme: PageTheme
    private let close: () -> Void
    private let leading: () -> Leading
    private let trailing: () -> Trailing
    public init(title: String, theme: PageTheme, close: @escaping () -> Void,
                @ViewBuilder leading: @escaping () -> Leading, @ViewBuilder trailing: @escaping () -> Trailing) {
        self.title = title; self.theme = theme; self.close = close; self.leading = leading; self.trailing = trailing
    }
    public var body: some View {
        HStack(spacing: 10) {
            leading()
            Text(title).font(.title3.weight(.semibold)).fixedSize(horizontal: false, vertical: true)
                .accessibilityAddTraits(.isHeader)
            Spacer(minLength: 4)
            trailing()
            Button(action: close) {
                Image(systemName: "xmark").font(.body.weight(.semibold))
                    .foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
                    .frame(width: 44, height: 44).contentShape(Rectangle())
            }
            .buttonStyle(.plain).accessibilityLabel("关闭").padding(.trailing, -10)
        }
    }
}

/// Segmented input: one capsule keeps paired values (region and number, code and send) together.
public struct AccountInputCapsule<Content: View>: View {
    public init(theme: PageTheme, @ViewBuilder content: @escaping () -> Content) {
        self.theme = theme; self.content = content
    }
    let theme: PageTheme
    @ViewBuilder let content: () -> Content

    public var body: some View {
        HStack(spacing: 0) { content() }
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Capsule().fill(Color(uiColor: UIColor(theme.settings.input))))
            .overlay(Capsule().strokeBorder(Color(uiColor: UIColor(theme.settings.inputBorder))))
    }
}

public struct AccountPrimaryButtonStyle: ButtonStyle {
    public init(theme: PageTheme) { self.theme = theme }
    let theme: PageTheme

    public func makeBody(configuration: Configuration) -> some View {
        AccountButtonBody(configuration: configuration, theme: theme, destructive: false)
    }
}

public struct AccountDestructiveButtonStyle: ButtonStyle {
    public init(theme: PageTheme) { self.theme = theme }
    let theme: PageTheme

    public func makeBody(configuration: Configuration) -> some View {
        AccountButtonBody(configuration: configuration, theme: theme, destructive: true)
    }
}

private struct AccountButtonBody: View {
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

public extension PageTheme {
    /// Error and destructive red shared by account rows, details and confirmations.
    var settingsDanger: Color { Color(uiColor: UIColor(settings.danger)) }
}
#endif
