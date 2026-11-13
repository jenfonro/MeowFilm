#if os(iOS)
import SwiftUI
import UIKit

/// Asset-backed platform mark with a neutral system-symbol fallback.
public struct AccountPlatformMark: View {
    private let assetName: String?
    private let symbol: String
    private let size: CGFloat
    private let theme: PageTheme
    public init(assetName: String? = nil, symbol: String, size: CGFloat = 28, theme: PageTheme) {
        self.assetName = assetName; self.symbol = symbol; self.size = size; self.theme = theme
    }
    public var body: some View {
        Group {
            if let assetName {
                Image(assetName, bundle: .main).renderingMode(.original).resizable().scaledToFit()
            } else {
                Image(systemName: symbol).foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}
#endif

#if os(iOS)
public struct AccountPlatformLabel: View {
    private let title: String
    private let assetName: String?
    private let symbol: String
    private let size: CGFloat
    private let theme: PageTheme
    public init(title: String, assetName: String? = nil, symbol: String, size: CGFloat = 28, theme: PageTheme) {
        self.title = title; self.assetName = assetName; self.symbol = symbol; self.size = size; self.theme = theme
    }
    public var body: some View {
        HStack(spacing: 12) {
            AccountPlatformMark(assetName: assetName, symbol: symbol, size: size, theme: theme)
            Text(title).fixedSize(horizontal: false, vertical: true)
        }
    }
}
#endif
