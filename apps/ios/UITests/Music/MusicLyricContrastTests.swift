#if os(iOS)
import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature

@MainActor
final class MusicLyricContrastTests: XCTestCase {
    func testPresetAndCustomLyricsRemainReadableOverActualComposedBackground() {
        let palettes = PageColorway.allCases.map(PagePalette.preset) +
            [0x000000, 0xffffff, 0xffff00, 0xff0000, 0x0000ff].map { PagePalette.custom(UInt32($0)) }
        for palette in palettes {
            for artwork in [false, true] {
                let colors = MusicPlayerBackgroundView.lyricColors(palette: palette, artwork: artwork)
                let endpoints = MusicPlayerBackgroundView.backgroundColors(palette: palette, style: .full)
                func rgb(_ value: UIColor) -> PageColor {
                    var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
                    value.getRed(&r, green: &g, blue: &b, alpha: &a)
                    return .init(rgb: UInt32((r * 255).rounded()) << 16 | UInt32((g * 255).rounded()) << 8 | UInt32((b * 255).rounded()))
                }
                let top = rgb(endpoints[0]), bottom = rgb(endpoints[1])
                for step in 0...10 {
                    let base = top.mixed(with: bottom, ratio: Double(step) / 10)
                    for pixel: UInt32 in [0, 0xffffff, 0xff0000, 0x00ff00, 0x0000ff] {
                        let background = artwork ? base.mixed(with: .init(rgb: pixel), ratio: 0.24) : base
                        for selected in [false, true] {
                            let actual = selected ? background.mixed(with: colors.highlight, ratio: 0.08) : background
                            XCTAssertGreaterThanOrEqual(colors.normal.contrastRatio(with: actual), 4.5, palette.title)
                            XCTAssertGreaterThanOrEqual(colors.highlight.contrastRatio(with: actual), 4.5, palette.title)
                            XCTAssertGreaterThan(colors.highlight.contrastRatio(with: actual), colors.normal.contrastRatio(with: actual))
                        }
                    }
                }
            }
        }
    }
}
#endif
