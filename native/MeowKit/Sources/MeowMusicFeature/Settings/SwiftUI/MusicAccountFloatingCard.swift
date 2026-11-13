#if os(iOS)
import SwiftUI
import UIKit
import MeowUI

struct MusicAccountCardHeader<Trailing: View>: View {
    let platform: MusicAccountPlatform
    let title: String
    let theme: PageTheme
    let close: () -> Void
    @ViewBuilder let trailing: () -> Trailing

    var body: some View {
        AccountCardHeader(title: title, theme: theme, close: close) {
            MusicPlatformLogo(assetName: platform.settingsLogoName, theme: theme)
        } trailing: {
            trailing()
        }
    }
}

#endif
